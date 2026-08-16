import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api } from "./client";
import type { QueryFilter, Row, RowList } from "./types";

const base = (projectId: string, databaseId: string, tableId: string) =>
  `/console/projects/${projectId}/databases/${databaseId}/tables/${tableId}/rows`;

const rowsKey = (projectId: string, databaseId: string, tableId: string) =>
  ["projects", projectId, "databases", databaseId, "tables", tableId, "rows"] as const;

export interface SortState {
  attribute: string;
  direction: "asc" | "desc";
}

const PAGE_SIZE = 50;

function serializeQueries(filters: QueryFilter[], sort: SortState | null, cursorAfter?: string): string[] {
  const queries = filters.map((f) => JSON.stringify(f));
  if (sort) queries.push(JSON.stringify({ method: sort.direction === "asc" ? "orderAsc" : "orderDesc", attribute: sort.attribute }));
  queries.push(JSON.stringify({ method: "limit", values: [PAGE_SIZE] }));
  if (cursorAfter) queries.push(JSON.stringify({ method: "cursorAfter", values: [cursorAfter] }));
  return queries;
}

function buildUrl(pathBase: string, queries: string[], total: boolean): string {
  const params = new URLSearchParams();
  for (const q of queries) params.append("queries[]", q);
  if (!total) params.append("total", "false");
  return `${pathBase}?${params.toString()}`;
}

export function useRows(projectId: string, databaseId: string, tableId: string, filters: QueryFilter[], sort: SortState | null) {
  return useInfiniteQuery({
    queryKey: [...rowsKey(projectId, databaseId, tableId), { filters, sort }],
    queryFn: ({ pageParam }) => {
      const cursor = pageParam as string | undefined;
      const queries = serializeQueries(filters, sort, cursor);
      return api<RowList>(buildUrl(base(projectId, databaseId, tableId), queries, /* total */ !cursor));
    },
    initialPageParam: undefined as string | undefined,
    getNextPageParam: (lastPage) => {
      const rows = lastPage.rows;
      return rows.length < PAGE_SIZE ? undefined : rows[rows.length - 1].$id;
    },
    enabled: !!databaseId && !!tableId,
  });
}

export function useRow(projectId: string, databaseId: string, tableId: string, rowId: string | null) {
  return useQuery({
    queryKey: [...rowsKey(projectId, databaseId, tableId), rowId],
    queryFn: () => api<Row>(`${base(projectId, databaseId, tableId)}/${rowId}`),
    enabled: !!rowId,
  });
}

export interface CreateRowInput {
  rowId?: string;
  data: Record<string, unknown>;
  permissions?: string[];
}

export function useCreateRow(projectId: string, databaseId: string, tableId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: CreateRowInput) =>
      api<Row>(base(projectId, databaseId, tableId), { method: "POST", body: input }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: rowsKey(projectId, databaseId, tableId) }),
  });
}

export interface UpdateRowInput {
  rowId: string;
  data?: Record<string, unknown>;
  permissions?: string[];
}

/** PATCH is genuinely partial — pass only the fields that actually changed in `data`. */
export function useUpdateRow(projectId: string, databaseId: string, tableId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ rowId, data, permissions }: UpdateRowInput) =>
      api<Row>(`${base(projectId, databaseId, tableId)}/${rowId}`, { method: "PATCH", body: { data, permissions } }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: rowsKey(projectId, databaseId, tableId) }),
  });
}

export function useDeleteRow(projectId: string, databaseId: string, tableId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (rowId: string) =>
      api<void>(`${base(projectId, databaseId, tableId)}/${rowId}`, { method: "DELETE" }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: rowsKey(projectId, databaseId, tableId) }),
  });
}

/** No bulk-delete endpoint on the backend — bulk select fires per-row deletes in parallel. */
export function useBulkDeleteRows(projectId: string, databaseId: string, tableId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (rowIds: string[]) =>
      Promise.all(rowIds.map((id) => api<void>(`${base(projectId, databaseId, tableId)}/${id}`, { method: "DELETE" }))),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: rowsKey(projectId, databaseId, tableId) }),
  });
}
