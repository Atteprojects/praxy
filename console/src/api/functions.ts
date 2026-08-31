import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api, ApiError } from "./client";
import type {
  ErrorEnvelope, FunctionCreatedFromTemplate, FunctionDeployment, FunctionDeploymentList, FunctionEnvVar,
  FunctionEnvVarList, FunctionExecution, FunctionExecutionList, FunctionGitBranches, FunctionList,
  FunctionRuntimeList, FunctionTemplateList, PraxyFunction,
} from "./types";

const base = (projectId: string) => `/console/projects/${projectId}/functions`;

export function useFunctions(projectId: string) {
  return useQuery({
    queryKey: ["projects", projectId, "functions"],
    queryFn: () => api<FunctionList>(base(projectId)),
    refetchInterval: 5_000,
  });
}

/** Base images are an operator config knob, not a fixed constant — see FunctionEndpoints.ListRuntimes. */
export function useFunctionRuntimes(projectId: string) {
  return useQuery({
    queryKey: ["projects", projectId, "functions", "runtimes"],
    queryFn: () => api<FunctionRuntimeList>(`${base(projectId)}/runtimes`),
    staleTime: 5 * 60_000,
  });
}

export function useFunction(projectId: string, functionId: string) {
  return useQuery({
    queryKey: ["projects", projectId, "functions", functionId],
    queryFn: () => api<PraxyFunction>(`${base(projectId)}/${functionId}`),
    refetchInterval: 5_000,
  });
}

export function useCreateFunction(projectId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: {
      key: string; name: string; runtime: string; entrypoint: string;
      timeoutSeconds?: number; events?: string[]; schedule?: string;
    }) => api<PraxyFunction>(base(projectId), { method: "POST", body: input }),
    // Skips the deleted function's own detail query. Invalidating the whole prefix refetches it,
    // which 404s and throws on the very screen that is navigating away — leaving the console
    // stranded on a route for a function that no longer exists.
    onSuccess: (_result, functionId) =>
      queryClient.invalidateQueries({
        queryKey: ["projects", projectId, "functions"],
        predicate: (query) => query.queryKey[3] !== functionId,
      }),
  });
}

/** The bundled starter catalog (FunctionTemplates.cs) — unauthenticated, project-agnostic, so it's fetched from `/functions/templates` rather than the console's project-scoped base. */
export function useFunctionTemplates() {
  return useQuery({
    queryKey: ["functions", "templates"],
    queryFn: () => api<FunctionTemplateList>("/functions/templates"),
    staleTime: 5 * 60_000,
  });
}

/**
 * Creates a function from a bundled starter and deploys its tar as the first deployment in one
 * call. Mirrors `useDeploySiteStarterTemplate`'s call-time-argument shape in spirit, but a function
 * doesn't exist yet to pass an id for — the whole create-and-deploy happens server-side in one request.
 */
export function useDeployFunctionTemplate(projectId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: { templateKey: string; key: string; name: string }) =>
      api<FunctionCreatedFromTemplate>(`${base(projectId)}/from-template`, { method: "POST", body: input }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["projects", projectId, "functions"] }),
  });
}

export function useUpdateFunction(projectId: string, functionId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: {
      name?: string; entrypoint?: string; timeoutSeconds?: number; events?: string[];
      execute?: string[]; schedule?: string; enabled?: boolean;
    }) => api<PraxyFunction>(`${base(projectId)}/${functionId}`, { method: "PATCH", body: input }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ["projects", projectId, "functions"] });
      void queryClient.invalidateQueries({ queryKey: ["projects", projectId, "functions", functionId] });
    },
  });
}

export function useDeleteFunction(projectId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (functionId: string) => api<void>(`${base(projectId)}/${functionId}`, { method: "DELETE" }),
    // Skips the deleted function's own detail query. Invalidating the whole prefix refetches it,
    // which 404s and throws on the very screen that is navigating away — leaving the console
    // stranded on a route for a function that no longer exists.
    onSuccess: (_result, functionId) =>
      queryClient.invalidateQueries({
        queryKey: ["projects", projectId, "functions"],
        predicate: (query) => query.queryKey[3] !== functionId,
      }),
  });
}

// ---- env vars ----

export function useFunctionEnvVars(projectId: string, functionId: string) {
  return useQuery({
    queryKey: ["projects", projectId, "functions", functionId, "env"],
    queryFn: () => api<FunctionEnvVarList>(`${base(projectId)}/${functionId}/env`),
  });
}

export function useSetEnvVar(projectId: string, functionId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ key, value }: { key: string; value: string }) =>
      api<FunctionEnvVar>(`${base(projectId)}/${functionId}/env/${encodeURIComponent(key)}`, {
        method: "PUT",
        body: { value },
      }),
    onSuccess: () =>
      queryClient.invalidateQueries({ queryKey: ["projects", projectId, "functions", functionId, "env"] }),
  });
}

export function useDeleteEnvVar(projectId: string, functionId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (key: string) =>
      api<void>(`${base(projectId)}/${functionId}/env/${encodeURIComponent(key)}`, { method: "DELETE" }),
    onSuccess: () =>
      queryClient.invalidateQueries({ queryKey: ["projects", projectId, "functions", functionId, "env"] }),
  });
}

// ---- git repository (Functions git integration) ----

/** A repository's real branches, fetched live through the connected GitHub App installation — used to populate the production-branch picker before connecting. Only fires once the input actually looks like "owner/repo", so it isn't refetched on every keystroke; a short staleTime avoids refetching once it has. */
export function useFunctionGitBranches(projectId: string, functionId: string, repositoryFullName: string) {
  return useQuery({
    queryKey: ["projects", projectId, "functions", functionId, "git", "branches", repositoryFullName],
    queryFn: () =>
      api<FunctionGitBranches>(`${base(projectId)}/${functionId}/git/branches?repository=${encodeURIComponent(repositoryFullName)}`),
    enabled: /^[^/\s]+\/[^/\s]+$/.test(repositoryFullName),
    staleTime: 30_000,
  });
}

export function useConnectFunctionGit(projectId: string, functionId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: { repositoryFullName: string; productionBranch: string }) =>
      api<PraxyFunction>(`${base(projectId)}/${functionId}/git`, { method: "POST", body: input }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ["projects", projectId, "functions", functionId] });
      void queryClient.invalidateQueries({ queryKey: ["projects", projectId, "functions"] });
    },
  });
}

export function useDisconnectFunctionGit(projectId: string, functionId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: () => api<PraxyFunction>(`${base(projectId)}/${functionId}/git`, { method: "DELETE" }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ["projects", projectId, "functions", functionId] });
      void queryClient.invalidateQueries({ queryKey: ["projects", projectId, "functions"] });
    },
  });
}

// ---- deployments ----

export function useFunctionDeployments(projectId: string, functionId: string) {
  return useQuery({
    queryKey: ["projects", projectId, "functions", functionId, "deployments"],
    queryFn: () => api<FunctionDeploymentList>(`${base(projectId)}/${functionId}/deployments`),
    refetchInterval: 3_000,
  });
}

/** Polls tightly while the build is in flight so the console's build-log tail reads as live. */
export function useFunctionDeployment(projectId: string, functionId: string, deploymentId: string | null) {
  return useQuery({
    queryKey: ["projects", projectId, "functions", functionId, "deployments", deploymentId],
    queryFn: () => api<FunctionDeployment>(`${base(projectId)}/${functionId}/deployments/${deploymentId}`),
    enabled: deploymentId !== null,
    refetchInterval: (query) =>
      query.state.data?.status === "queued" || query.state.data?.status === "building" ? 1_000 : false,
  });
}

async function uploadDeploymentTar(url: string, file: File): Promise<FunctionDeployment> {
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
  return (await response.json()) as FunctionDeployment;
}

export function useCreateDeployment(projectId: string, functionId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (file: File) => uploadDeploymentTar(`${base(projectId)}/${functionId}/deployments`, file),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ["projects", projectId, "functions", functionId, "deployments"] });
      void queryClient.invalidateQueries({ queryKey: ["projects", projectId, "functions", functionId] });
    },
  });
}

export function useActivateDeployment(projectId: string, functionId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (deploymentId: string) =>
      api<FunctionDeployment>(`${base(projectId)}/${functionId}/deployments/${deploymentId}/activate`, { method: "POST" }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ["projects", projectId, "functions", functionId, "deployments"] });
      void queryClient.invalidateQueries({ queryKey: ["projects", projectId, "functions", functionId] });
    },
  });
}

// ---- executions ----

export function useFunctionExecutions(projectId: string, functionId: string) {
  return useQuery({
    queryKey: ["projects", projectId, "functions", functionId, "executions"],
    queryFn: () => api<FunctionExecutionList>(`${base(projectId)}/${functionId}/executions`),
    refetchInterval: 3_000,
  });
}

export function useFunctionExecution(projectId: string, functionId: string, executionId: string | null) {
  return useQuery({
    queryKey: ["projects", projectId, "functions", functionId, "executions", executionId],
    queryFn: () => api<FunctionExecution>(`${base(projectId)}/${functionId}/executions/${executionId}`),
    enabled: executionId !== null,
    refetchInterval: (query) =>
      query.state.data?.status === "waiting" || query.state.data?.status === "processing" ? 1_000 : false,
  });
}

export function useInvokeFunction(projectId: string, functionId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: { method?: string; path?: string; body?: string; async?: boolean }) =>
      api<FunctionExecution>(
        `${base(projectId)}/${functionId}/executions${input.async ? "?async=true" : ""}`,
        { method: "POST", body: { method: input.method, path: input.path, body: input.body } },
      ),
    onSuccess: () =>
      queryClient.invalidateQueries({ queryKey: ["projects", projectId, "functions", functionId, "executions"] }),
  });
}
