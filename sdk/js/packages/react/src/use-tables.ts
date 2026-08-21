"use client";

import type { Query, Row, RowList, TableRef } from "@praxy/core";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { UseQueryResult } from "@tanstack/react-query";
import { usePraxyClient } from "./provider.js";

/** A query-key prefix shared by every hook for a table — invalidating this prefix covers list, row, and filtered variants. */
export function tableQueryKey(table: TableRef<unknown>): unknown[] {
  return ["praxy", "rows", table.databaseId, table.tableId];
}

export function useRows<T>(
  table: TableRef<T>,
  options: { queries?: Query[]; total?: boolean } = {},
): UseQueryResult<RowList<T>> {
  const client = usePraxyClient();
  const encodedQueries = options.queries?.map((q) => q.encode());
  return useQuery({
    queryKey: [...tableQueryKey(table), { queries: encodedQueries, total: options.total }],
    queryFn: () => client.tables.list<T>(table, options),
  });
}

export function useRow<T>(table: TableRef<T>, rowId: string | null): UseQueryResult<Row<T>> {
  const client = usePraxyClient();
  return useQuery({
    queryKey: [...tableQueryKey(table), rowId],
    queryFn: () => client.tables.get<T>(table, rowId as string),
    enabled: rowId != null,
  });
}

export function useCreateRow<T>(table: TableRef<T>) {
  const client = usePraxyClient();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: { rowId?: string; data: T; permissions?: string[] }) =>
      client.tables.create<T>(table, input),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: tableQueryKey(table) }),
  });
}

export function useUpdateRow<T>(table: TableRef<T>) {
  const client = usePraxyClient();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ rowId, ...input }: { rowId: string; data?: Partial<T>; permissions?: string[] }) =>
      client.tables.update<T>(table, rowId, input),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: tableQueryKey(table) }),
  });
}

export function useDeleteRow<T>(table: TableRef<T>) {
  const client = usePraxyClient();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (rowId: string) => client.tables.delete<T>(table, rowId),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: tableQueryKey(table) }),
  });
}
