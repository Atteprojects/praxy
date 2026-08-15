import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api, ApiError } from "./client";
import type { Account, Capabilities, Project, ProjectList } from "./types";

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
