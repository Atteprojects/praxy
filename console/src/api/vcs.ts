import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api } from "./client";
import type { GithubInstallationList, GithubInstallUrl } from "./types";

// Instance-wide — no projectId in these paths. The same GitHub App installation status shows no
// matter which project's console you're looking at (Sites Phase 4's own explicit design call).

export function useGithubInstallations() {
  return useQuery({
    queryKey: ["vcs", "github", "installations"],
    queryFn: () => api<GithubInstallationList>("/console/vcs/github/installations"),
    refetchInterval: 5_000,
  });
}

export function useRemoveGithubInstallation() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => api<void>(`/console/vcs/github/installations/${id}`, { method: "DELETE" }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ["vcs", "github", "installations"] });
    },
  });
}

export function useGithubInstallUrl() {
  return useQuery({
    queryKey: ["vcs", "github", "install-url"],
    queryFn: () => api<GithubInstallUrl>("/console/vcs/github/install-url"),
    // The App itself is fixed at startup — no need to keep polling this one, and a failure here
    // (typically: the instance's own App isn't configured yet) won't resolve itself by retrying.
    staleTime: Infinity,
    retry: false,
  });
}
