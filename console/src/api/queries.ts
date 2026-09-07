import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api, ApiError } from "./client";
import type {
  Account,
  Capabilities,
  CreateProjectInput,
  Organization,
  OrganizationList,
  OrganizationMember,
  OrganizationMemberList,
  Project,
  ProjectList,
  QuotaSnapshot,
} from "./types";

export function useCapabilities() {
  return useQuery({
    queryKey: ["capabilities"],
    queryFn: () => api<Capabilities>("/console/capabilities"),
    staleTime: 60_000,
  });
}

/** Resolves to null (not an error) when there is no session. */
export function useAccount() {
  return useQuery({
    queryKey: ["account"],
    queryFn: async () => {
      try {
        return await api<Account>("/console/account");
      } catch (error) {
        if (error instanceof ApiError && error.code === 401) return null;
        throw error;
      }
    },
    staleTime: 30_000,
    retry: false,
  });
}

/**
 * The organizations the operator belongs to. The console home resolves this to build the
 * org-scoped URL (remembered last org, else a picker once there's more than one), so this is on
 * the critical path of the first screen after login.
 */
export function useOrganizations() {
  return useQuery({
    queryKey: ["organizations"],
    queryFn: () => api<OrganizationList>("/console/organizations"),
    staleTime: 60_000,
  });
}

export function useOrganization(organizationId: string) {
  return useQuery({
    queryKey: ["organizations", organizationId],
    queryFn: () => api<Organization>(`/console/organizations/${organizationId}`),
    staleTime: 60_000,
  });
}

export function useCreateOrganization() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: { name: string }) =>
      api<Organization>("/console/organizations", { method: "POST", body: input }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["organizations"] }),
  });
}

export function useUpdateOrganization(organizationId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: { name: string }) =>
      api<Organization>(`/console/organizations/${organizationId}`, { method: "PATCH", body: input }),
    onSuccess: (data) => {
      queryClient.setQueryData(["organizations", organizationId], data);
      void queryClient.invalidateQueries({ queryKey: ["organizations"] });
    },
  });
}

/** No `force` — the server refuses a non-empty or last-remaining organization outright, no override. */
export function useDeleteOrganization() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (organizationId: string) =>
      api<void>(`/console/organizations/${organizationId}`, { method: "DELETE" }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["organizations"] }),
  });
}

export function useOrganizationMembers(organizationId: string) {
  return useQuery({
    queryKey: ["organizations", organizationId, "members"],
    queryFn: () => api<OrganizationMemberList>(`/console/organizations/${organizationId}/members`),
  });
}

/** Owner only — the server is the actual gate; a member calling this gets a 403. */
export function useInviteOrganizationMember(organizationId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: { email: string; role: string; url: string }) =>
      api<OrganizationMember>(`/console/organizations/${organizationId}/members`, {
        method: "POST",
        body: input,
      }),
    onSuccess: () =>
      queryClient.invalidateQueries({ queryKey: ["organizations", organizationId, "members"] }),
  });
}

export function useUpdateOrganizationMemberRole(organizationId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ userId, role }: { userId: string; role: string }) =>
      api<OrganizationMember>(`/console/organizations/${organizationId}/members/${userId}`, {
        method: "PATCH",
        body: { role },
      }),
    onSuccess: () =>
      queryClient.invalidateQueries({ queryKey: ["organizations", organizationId, "members"] }),
  });
}

/** Removing yourself (leaving) is allowed; the server still enforces the last-owner rule either way. */
export function useRemoveOrganizationMember(organizationId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (userId: string) =>
      api<void>(`/console/organizations/${organizationId}/members/${userId}`, { method: "DELETE" }),
    onSuccess: () =>
      queryClient.invalidateQueries({ queryKey: ["organizations", organizationId, "members"] }),
  });
}

/** No session required — the invite's own secret is the credential. Signs the invitee in on success. */
export function useAcceptOrganizationInvite() {
  return useSessionMutation<{ organizationId: string; userId: string; secret: string; password?: string }>(
    ({ organizationId, userId, secret, password }) => ({
      url: `/console/organizations/${organizationId}/members/${userId}/accept`,
      body: { secret, password },
    }),
  );
}

export function useProjects(enabled = true) {
  return useQuery({
    queryKey: ["projects"],
    queryFn: () => api<ProjectList>("/console/projects"),
    enabled,
  });
}

export function useProject(projectId: string, options: { pollWhileUnpinged?: boolean } = {}) {
  return useQuery({
    queryKey: ["projects", projectId],
    queryFn: () => api<Project>(`/console/projects/${projectId}`),
    // The overview's "waiting for first ping" state: poll until the ping lands,
    // then stop — the query result itself advances the UI.
    refetchInterval: options.pollWhileUnpinged
      ? (query) => (query.state.data?.lastPingAt ? false : 3_000)
      : false,
  });
}

/** Org-level quota usage for this project (roadmap Phase 9) — static enough per page-load, no polling. */
export function useQuotas(projectId: string) {
  return useQuery({
    queryKey: ["projects", projectId, "quotas"],
    queryFn: () => api<QuotaSnapshot>(`/console/projects/${projectId}/quotas`),
  });
}

/** Live connection count for the project overview's stat tile — polled, not pushed, since it's a cheap number and not worth its own WS subscription. */
export function useConnectionCount(projectId: string) {
  return useQuery({
    queryKey: ["projects", projectId, "realtime", "connections"],
    queryFn: () => api<{ count: number }>(`/console/projects/${projectId}/realtime/connections`),
    refetchInterval: 5_000,
  });
}

/**
 * Seed the account cache from the response rather than refetching: navigating into the
 * authed shell must never race a stale `null` account (which would bounce back to /login).
 */
function useSessionMutation<TInput>(path: (input: TInput) => { url: string; body?: unknown }) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: TInput) => {
      const { url, body } = path(input);
      return api<{ account: Account }>(url, { method: "POST", body });
    },
    onSuccess: (data) => {
      queryClient.setQueryData(["account"], data.account);
      void queryClient.invalidateQueries({ queryKey: ["capabilities"] });
      void queryClient.invalidateQueries({ queryKey: ["projects"] });
      // Organizations-phase-2: an org's `role` is per-caller, so a login swap (or an invite
      // acceptance, which mints a session the same way) without a full page reload must not
      // leave a previous account's cached role sitting under the same query key.
      void queryClient.invalidateQueries({ queryKey: ["organizations"] });
    },
  });
}

export function useClaim() {
  return useSessionMutation<{ email: string; password: string; name?: string; setupToken?: string }>(
    (input) => ({ url: "/console/claim", body: input }),
  );
}

export function useLogin() {
  return useSessionMutation<{ email: string; password: string }>((input) => ({
    url: "/console/sessions",
    body: input,
  }));
}

export function useLogout() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: () => api<void>("/console/sessions/current", { method: "DELETE" }),
    onSuccess: () => {
      queryClient.setQueryData(["account"], null);
      void queryClient.invalidateQueries({ queryKey: ["projects"], refetchType: "none" });
      void queryClient.invalidateQueries({ queryKey: ["organizations"], refetchType: "none" });
    },
  });
}

export function useCreateProject() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: CreateProjectInput) =>
      api<Project>("/console/projects", { method: "POST", body: input }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["projects"] }),
  });
}

export function useUpdateProject(projectId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: { name: string }) =>
      api<Project>(`/console/projects/${projectId}`, { method: "PATCH", body: input }),
    onSuccess: (data) => {
      queryClient.setQueryData(["projects", projectId], data);
      void queryClient.invalidateQueries({ queryKey: ["projects"] });
    },
  });
}

export function useDeleteProject(projectId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: () => api<void>(`/console/projects/${projectId}?force=true`, { method: "DELETE" }),
    // Skips the deleted project's own subtree — invalidating it would refetch and 404 on whichever
    // project-scoped screen is still mounted while the console navigates away.
    onSuccess: () =>
      queryClient.invalidateQueries({
        queryKey: ["projects"],
        predicate: (query) => query.queryKey[1] !== projectId,
      }),
  });
}
