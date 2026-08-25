import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api, ApiError } from "./client";
import type {
  ErrorEnvelope, PraxySite, SiteDeployment, SiteDeploymentList, SiteDomain, SiteDomainList, SiteEnvVar,
  SiteEnvVarList, SiteList,
} from "./types";

const base = (projectId: string) => `/console/projects/${projectId}/sites`;

export function useSites(projectId: string) {
  return useQuery({
    queryKey: ["projects", projectId, "sites"],
    queryFn: () => api<SiteList>(base(projectId)),
    refetchInterval: 5_000,
  });
}

export function useSite(projectId: string, siteId: string) {
  return useQuery({
    queryKey: ["projects", projectId, "sites", siteId],
    queryFn: () => api<PraxySite>(`${base(projectId)}/${siteId}`),
    refetchInterval: 5_000,
  });
}

export function useCreateSite(projectId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: { key: string; name: string; rootDirectory?: string }) =>
      api<PraxySite>(base(projectId), { method: "POST", body: input }),
    onSuccess: (_result, siteId) =>
      queryClient.invalidateQueries({
        queryKey: ["projects", projectId, "sites"],
        predicate: (query) => query.queryKey[3] !== siteId,
      }),
  });
}

export function useUpdateSite(projectId: string, siteId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: { name?: string; rootDirectory?: string; enabled?: boolean }) =>
      api<PraxySite>(`${base(projectId)}/${siteId}`, { method: "PATCH", body: input }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ["projects", projectId, "sites"] });
      void queryClient.invalidateQueries({ queryKey: ["projects", projectId, "sites", siteId] });
    },
  });
}

export function useDeleteSite(projectId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (siteId: string) => api<void>(`${base(projectId)}/${siteId}`, { method: "DELETE" }),
    onSuccess: (_result, siteId) =>
      queryClient.invalidateQueries({
        queryKey: ["projects", projectId, "sites"],
        predicate: (query) => query.queryKey[3] !== siteId,
      }),
  });
}

// ---- env vars ----

export function useSiteEnvVars(projectId: string, siteId: string) {
  return useQuery({
    queryKey: ["projects", projectId, "sites", siteId, "env"],
    queryFn: () => api<SiteEnvVarList>(`${base(projectId)}/${siteId}/env`),
  });
}

export function useSetSiteEnvVar(projectId: string, siteId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ key, value }: { key: string; value: string }) =>
      api<SiteEnvVar>(`${base(projectId)}/${siteId}/env/${encodeURIComponent(key)}`, {
        method: "PUT",
        body: { value },
      }),
    onSuccess: () =>
      queryClient.invalidateQueries({ queryKey: ["projects", projectId, "sites", siteId, "env"] }),
  });
}

export function useDeleteSiteEnvVar(projectId: string, siteId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (key: string) =>
      api<void>(`${base(projectId)}/${siteId}/env/${encodeURIComponent(key)}`, { method: "DELETE" }),
    onSuccess: () =>
      queryClient.invalidateQueries({ queryKey: ["projects", projectId, "sites", siteId, "env"] }),
  });
}

// ---- custom domains (Sites Phase 3) ----

export function useSiteDomains(projectId: string, siteId: string) {
  return useQuery({
    queryKey: ["projects", projectId, "sites", siteId, "domains"],
    queryFn: () => api<SiteDomainList>(`${base(projectId)}/${siteId}/domains`),
    // Polls so a domain's pending -> verified flip (which happens server-side on the first proxied
    // request through it, not a console action) shows up without a manual refresh.
    refetchInterval: 5_000,
  });
}

export function useAddSiteDomain(projectId: string, siteId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (hostname: string) =>
      api<SiteDomain>(`${base(projectId)}/${siteId}/domains`, { method: "POST", body: { hostname } }),
    onSuccess: () =>
      queryClient.invalidateQueries({ queryKey: ["projects", projectId, "sites", siteId, "domains"] }),
  });
}

export function useDeleteSiteDomain(projectId: string, siteId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (domainId: string) =>
      api<void>(`${base(projectId)}/${siteId}/domains/${domainId}`, { method: "DELETE" }),
    onSuccess: () =>
      queryClient.invalidateQueries({ queryKey: ["projects", projectId, "sites", siteId, "domains"] }),
  });
}

// ---- deployments ----

export function useSiteDeployments(projectId: string, siteId: string) {
  return useQuery({
    queryKey: ["projects", projectId, "sites", siteId, "deployments"],
    queryFn: () => api<SiteDeploymentList>(`${base(projectId)}/${siteId}/deployments`),
    refetchInterval: 3_000,
  });
}

/**
 * Polls tightly while the build is in flight so the console's build-log tail reads as live — and
 * keeps polling a little past `status: "ready"` too. Auto-activation (which sets `activatedAt` and
 * starts the real container) is a separate step *after* the build worker marks the row ready, so a
 * deployment can sit in "ready, not yet activated" for a moment; stopping the poll the instant
 * `status` flips leaves the sheet showing a stale snapshot from just before activation finished —
 * the success view would render with no build-duration line until the next unrelated refetch.
 */
export function useSiteDeployment(projectId: string, siteId: string, deploymentId: string | null) {
  return useQuery({
    queryKey: ["projects", projectId, "sites", siteId, "deployments", deploymentId],
    queryFn: () => api<SiteDeployment>(`${base(projectId)}/${siteId}/deployments/${deploymentId}`),
    enabled: deploymentId !== null,
    refetchInterval: (query) => {
      const data = query.state.data;
      if (!data) return 1_000;
      if (data.status === "queued" || data.status === "building") return 1_000;
      if (data.status === "ready" && !data.activatedAt) return 1_000;
      return false;
    },
  });
}

async function uploadDeploymentTar(url: string, file: File): Promise<SiteDeployment> {
  const response = await fetch(`/v1${url}`, {
    method: "POST",
    credentials: "include",
    headers: { "Content-Type": "application/x-tar" },
    body: file,
  });
  if (!response.ok) {
    let envelope: ErrorEnvelope;
    try {
      envelope = (await response.json()) as ErrorEnvelope;
    } catch {
      envelope = {
        message: `Upload failed with status ${response.status}`,
        code: response.status,
        type: "general_server_error",
        version: "",
        requestId: response.headers.get("X-Praxy-Request-Id") ?? "",
      };
    }
    throw new ApiError(envelope);
  }
  return (await response.json()) as SiteDeployment;
}

export function useCreateSiteDeployment(projectId: string, siteId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (file: File) => uploadDeploymentTar(`${base(projectId)}/${siteId}/deployments`, file),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ["projects", projectId, "sites", siteId, "deployments"] });
      void queryClient.invalidateQueries({ queryKey: ["projects", projectId, "sites", siteId] });
    },
  });
}

/** Deploys the bundled Next.js starter template as this site's first deployment — no file to pick, the server sources the tar itself. */
/**
 * `siteId` is a call-time argument to `mutateAsync`, not a hook parameter — unlike most mutations
 * here, the caller doesn't always know the site id up front (the create-site modal deploys the
 * template for a site it just created in the same submit, before any route/param carries its id).
 */
export function useDeploySiteStarterTemplate(projectId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (siteId: string) =>
      api<SiteDeployment>(`${base(projectId)}/${siteId}/deployments/from-starter-template`, { method: "POST" }),
    onSuccess: (_result, siteId) => {
      void queryClient.invalidateQueries({ queryKey: ["projects", projectId, "sites", siteId, "deployments"] });
      void queryClient.invalidateQueries({ queryKey: ["projects", projectId, "sites", siteId] });
    },
  });
}

export function useActivateSiteDeployment(projectId: string, siteId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (deploymentId: string) =>
      api<SiteDeployment>(`${base(projectId)}/${siteId}/deployments/${deploymentId}/activate`, { method: "POST" }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ["projects", projectId, "sites", siteId, "deployments"] });
      void queryClient.invalidateQueries({ queryKey: ["projects", projectId, "sites", siteId] });
    },
  });
}
