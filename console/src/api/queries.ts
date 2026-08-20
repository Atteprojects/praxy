import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api, ApiError } from "./client";
import type {
  Account,
  Capabilities,
  Organization,
  OrganizationList,
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
 * The organizations the operator belongs to — exactly one today. The console home resolves it to
 * build the org-scoped URL, so this is on the critical path of the first screen after login.
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
    },
  });
}

export function useCreateProject() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: { name: string; projectId?: string }) =>
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
