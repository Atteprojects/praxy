import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api } from "./client";
import type {
  ColumnList, ColumnSchema, ColumnType, Database, DatabaseList, IndexList, IndexSchema,
  SchemaJob, SchemaJobList, TableList, TablePermissions, TableSchema,
} from "./types";

/** All Phase 2 admin calls live under the project's console prefix, like Phase 1's. */
const base = (projectId: string) => `/console/projects/${projectId}/databases`;

// ---- databases ------------------------------------------------------------------------------

export function useDatabases(projectId: string) {
  return useQuery({
    queryKey: ["projects", projectId, "databases"],
    queryFn: () => api<DatabaseList>(base(projectId)),
  });
}

export function useDatabase(projectId: string, databaseId: string) {
  return useQuery({
    queryKey: ["projects", projectId, "databases", databaseId],
    queryFn: () => api<Database>(`${base(projectId)}/${databaseId}`),
    enabled: !!databaseId,
  });
}

export function useCreateDatabase(projectId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: { key: string; name: string }) =>
      api<Database>(base(projectId), { method: "POST", body: input }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["projects", projectId, "databases"] }),
  });
}

export function useUpdateDatabase(projectId: string, databaseId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: { name: string }) =>
      api<Database>(`${base(projectId)}/${databaseId}`, { method: "PATCH", body: input }),
    onSuccess: (data) => {
      queryClient.setQueryData(["projects", projectId, "databases", databaseId], data);
      void queryClient.invalidateQueries({ queryKey: ["projects", projectId, "databases"] });
    },
  });
}

export function useDeleteDatabase(projectId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (databaseId: string) =>
      api<void>(`${base(projectId)}/${databaseId}?force=true`, { method: "DELETE" }),
    // Skips every query belonging to the deleted database — its own detail row, its tables, its
    // jobs. Invalidating the whole prefix refetches them, which 404s and throws on whichever
    // database-scoped screen is mounted, leaving the console stranded on a route for a database
    // that no longer exists.
    onSuccess: (_result, databaseId) =>
      queryClient.invalidateQueries({
        queryKey: ["projects", projectId, "databases"],
        predicate: (query) => query.queryKey[3] !== databaseId,
      }),
  });
}

// ---- tables ---------------------------------------------------------------------------------

export function useTables(projectId: string, databaseId: string) {
  return useQuery({
    queryKey: ["projects", projectId, "databases", databaseId, "tables"],
    queryFn: () => api<TableList>(`${base(projectId)}/${databaseId}/tables`),
    enabled: !!databaseId,
  });
}

export function useTable(projectId: string, databaseId: string, tableId: string) {
  return useQuery({
    queryKey: ["projects", projectId, "databases", databaseId, "tables", tableId],
    queryFn: () => api<TableSchema>(`${base(projectId)}/${databaseId}/tables/${tableId}`),
    enabled: !!databaseId && !!tableId,
  });
}

export function useCreateTable(projectId: string, databaseId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: { key: string; name: string }) =>
      api<TableSchema>(`${base(projectId)}/${databaseId}/tables`, { method: "POST", body: input }),
    // Skips the deleted table's own detail query. Invalidating the whole prefix refetches it,
    // which 404s and throws on the very screen that is navigating away — leaving the console
    // stranded on a route for a table that no longer exists.
    onSuccess: (_result, tableId) =>
      queryClient.invalidateQueries({
        queryKey: ["projects", projectId, "databases", databaseId, "tables"],
        predicate: (query) => query.queryKey[5] !== tableId,
      }),
  });
}

export function useUpdateTable(projectId: string, databaseId: string, tableId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: { name?: string; enabled?: boolean }) =>
      api<TableSchema>(`${base(projectId)}/${databaseId}/tables/${tableId}`, { method: "PATCH", body: input }),
    onSuccess: (data) => {
      queryClient.setQueryData(["projects", projectId, "databases", databaseId, "tables", tableId], data);
      void queryClient.invalidateQueries({ queryKey: ["projects", projectId, "databases", databaseId, "tables"] });
    },
  });
}

export function useDeleteTable(projectId: string, databaseId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (tableId: string) =>
      api<void>(`${base(projectId)}/${databaseId}/tables/${tableId}?force=true`, { method: "DELETE" }),
    // Skips the deleted table's own detail query. Invalidating the whole prefix refetches it,
    // which 404s and throws on the very screen that is navigating away — leaving the console
    // stranded on a route for a table that no longer exists.
    onSuccess: (_result, tableId) =>
      queryClient.invalidateQueries({
        queryKey: ["projects", projectId, "databases", databaseId, "tables"],
        predicate: (query) => query.queryKey[5] !== tableId,
      }),
  });
}

// ---- table permissions ------------------------------------------------------------------------

export function useTablePermissions(projectId: string, databaseId: string, tableId: string) {
  return useQuery({
    queryKey: ["projects", projectId, "databases", databaseId, "tables", tableId, "permissions"],
    queryFn: () => api<TablePermissions>(`${base(projectId)}/${databaseId}/tables/${tableId}/permissions`),
    enabled: !!databaseId && !!tableId,
  });
}

export function useUpdateTablePermissions(projectId: string, databaseId: string, tableId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: { rowSecurity?: boolean; permissions?: string[] }) =>
      api<TablePermissions>(`${base(projectId)}/${databaseId}/tables/${tableId}/permissions`, {
        method: "PATCH",
        body: input,
      }),
    onSuccess: (data) => {
      queryClient.setQueryData(
        ["projects", projectId, "databases", databaseId, "tables", tableId, "permissions"], data);
      void queryClient.invalidateQueries({
        queryKey: ["projects", projectId, "databases", databaseId, "tables", tableId],
      });
    },
  });
}

// ---- columns --------------------------------------------------------------------------------

export function useColumns(projectId: string, databaseId: string, tableId: string) {
  return useQuery({
    queryKey: ["projects", projectId, "databases", databaseId, "tables", tableId, "columns"],
    queryFn: () => api<ColumnList>(`${base(projectId)}/${databaseId}/tables/${tableId}/columns`),
    enabled: !!databaseId && !!tableId,
    // Processing columns are rare in Phase 2 (only async DDL touches status), but poll gently
    // whenever one is in flight so the grid catches the flip to available/failed on its own.
    refetchInterval: (query) =>
      query.state.data?.columns.some((c) => c.status === "processing") ? 1_500 : false,
  });
}

export interface CreateColumnInput {
  key: string;
  required?: boolean;
  array?: boolean;
  default?: unknown;
  size?: number;
  elements?: string[];
  targetTableId?: string;
}

export function useCreateColumn(projectId: string, databaseId: string, tableId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ type, ...input }: CreateColumnInput & { type: ColumnType }) =>
      api<ColumnSchema>(`${base(projectId)}/${databaseId}/tables/${tableId}/columns/${type}`, {
        method: "POST",
        body: input,
      }),
    onSuccess: () =>
      queryClient.invalidateQueries({
        queryKey: ["projects", projectId, "databases", databaseId, "tables", tableId, "columns"],
      }),
  });
}

export function useUpdateColumn(projectId: string, databaseId: string, tableId: string, columnId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: { key?: string; required?: boolean }) =>
      api<ColumnSchema>(`${base(projectId)}/${databaseId}/tables/${tableId}/columns/${columnId}`, {
        method: "PATCH",
        body: input,
      }),
    onSuccess: () =>
      queryClient.invalidateQueries({
        queryKey: ["projects", projectId, "databases", databaseId, "tables", tableId, "columns"],
      }),
  });
}

export function useDeleteColumn(projectId: string, databaseId: string, tableId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ columnId, force }: { columnId: string; force?: boolean }) =>
      api<void>(`${base(projectId)}/${databaseId}/tables/${tableId}/columns/${columnId}${force ? "?force=true" : ""}`,
        { method: "DELETE" }),
    onSuccess: () =>
      queryClient.invalidateQueries({
        queryKey: ["projects", projectId, "databases", databaseId, "tables", tableId, "columns"],
      }),
  });
}

// ---- indexes --------------------------------------------------------------------------------

export function useIndexes(projectId: string, databaseId: string, tableId: string) {
  return useQuery({
    queryKey: ["projects", projectId, "databases", databaseId, "tables", tableId, "indexes"],
    queryFn: () => api<IndexList>(`${base(projectId)}/${databaseId}/tables/${tableId}/indexes`),
    enabled: !!databaseId && !!tableId,
    refetchInterval: (query) =>
      query.state.data?.indexes.some((i) => i.status === "processing") ? 1_500 : false,
  });
}

export function useCreateIndex(projectId: string, databaseId: string, tableId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: { key: string; type: string; columns: string[]; orders?: string[] }) =>
      api<IndexSchema>(`${base(projectId)}/${databaseId}/tables/${tableId}/indexes`, {
        method: "POST",
        body: input,
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({
        queryKey: ["projects", projectId, "databases", databaseId, "tables", tableId, "indexes"],
      });
      void queryClient.invalidateQueries({
        queryKey: ["projects", projectId, "databases", databaseId, "jobs"],
      });
    },
  });
}

export function useDeleteIndex(projectId: string, databaseId: string, tableId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (indexId: string) =>
      api<void>(`${base(projectId)}/${databaseId}/tables/${tableId}/indexes/${indexId}`, { method: "DELETE" }),
    onSuccess: () =>
      queryClient.invalidateQueries({
        queryKey: ["projects", projectId, "databases", databaseId, "tables", tableId, "indexes"],
      }),
  });
}

// ---- schema jobs ----------------------------------------------------------------------------

export function useSchemaJobs(projectId: string, databaseId: string, tableId?: string) {
  return useQuery({
    queryKey: ["projects", projectId, "databases", databaseId, "jobs", { tableId }],
    queryFn: () =>
      api<SchemaJobList>(
        `${base(projectId)}/${databaseId}/jobs${tableId ? `?tableId=${tableId}` : ""}`,
      ),
    enabled: !!databaseId,
    refetchInterval: (query) =>
      query.state.data?.jobs.some((j) => j.status === "queued" || j.status === "processing") ? 1_500 : false,
  });
}

function invalidateJobRelatedQueries(
  queryClient: ReturnType<typeof useQueryClient>, projectId: string, databaseId: string, tableId?: string,
) {
  void queryClient.invalidateQueries({ queryKey: ["projects", projectId, "databases", databaseId, "jobs"] });
  if (tableId) {
    void queryClient.invalidateQueries({
      queryKey: ["projects", projectId, "databases", databaseId, "tables", tableId, "indexes"],
    });
  }
}

export function useCancelSchemaJob(projectId: string, databaseId: string, tableId?: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (jobId: string) =>
      api<SchemaJob>(`${base(projectId)}/${databaseId}/jobs/${jobId}/cancel`, { method: "POST" }),
    onSuccess: () => invalidateJobRelatedQueries(queryClient, projectId, databaseId, tableId),
  });
}

export function useRetrySchemaJob(projectId: string, databaseId: string, tableId?: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (jobId: string) =>
      api<SchemaJob>(`${base(projectId)}/${databaseId}/jobs/${jobId}/retry`, { method: "POST" }),
    onSuccess: () => invalidateJobRelatedQueries(queryClient, projectId, databaseId, tableId),
  });
}
