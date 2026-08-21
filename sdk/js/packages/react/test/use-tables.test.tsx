import { tableRef } from "@praxy/core";
import { act, renderHook, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { describe, expect, it } from "vitest";
import { PraxyProvider } from "../src/provider";
import { useCreateRow, useDeleteRow, useRow, useRows, useUpdateRow } from "../src/use-tables";
import { emptyResponse, FakeTransport, jsonResponse } from "./support/fake-transport";

interface Todo {
  title: string;
  done: boolean;
}

const table = tableRef<Todo>("db_1", "tbl_1");
const rowMeta = { $id: "row_1", $tableId: "tbl_1", $databaseId: "db_1", $createdAt: "t", $updatedAt: "t", $permissions: [] };

function wrapperWith(transport: FakeTransport) {
  return function Wrapper({ children }: { children: ReactNode }) {
    return (
      <PraxyProvider config={{ endpoint: "https://api.test", projectId: "proj_1" }} transport={transport}>
        {children}
      </PraxyProvider>
    );
  };
}

describe("useRows", () => {
  it("fetches the row list through the provider's client", async () => {
    const transport = new FakeTransport(() =>
      jsonResponse(200, { total: 1, rows: [{ ...rowMeta, title: "Buy milk", done: false }] }),
    );
    const { result } = renderHook(() => useRows(table), { wrapper: wrapperWith(transport) });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data?.rows[0]?.title).toBe("Buy milk");
    expect(transport.requests[0]?.path).toBe("/v1/databases/db_1/tables/tbl_1/rows");
  });
});

describe("useRow", () => {
  it("is disabled while rowId is null", async () => {
    const transport = new FakeTransport(() => jsonResponse(200, { ...rowMeta, title: "x", done: false }));
    const { result } = renderHook(() => useRow(table, null), { wrapper: wrapperWith(transport) });
    expect(result.current.fetchStatus).toBe("idle");
    expect(transport.requests).toHaveLength(0);
  });
});

describe("row mutations", () => {
  it("useCreateRow POSTs and invalidates the list", async () => {
    const transport = new FakeTransport((req) => {
      if (req.method === "POST") return jsonResponse(201, { ...rowMeta, title: "Buy milk", done: false });
      return jsonResponse(200, { total: 1, rows: [{ ...rowMeta, title: "Buy milk", done: false }] });
    });
    const { result } = renderHook(() => useCreateRow(table), { wrapper: wrapperWith(transport) });

    await act(async () => {
      await result.current.mutateAsync({ data: { title: "Buy milk", done: false } });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    const posted = transport.requests.find((r) => r.method === "POST");
    expect(posted?.body).toEqual({ data: { title: "Buy milk", done: false } });
  });

  it("useUpdateRow PATCHes only the changed fields", async () => {
    const transport = new FakeTransport(() => jsonResponse(200, { ...rowMeta, title: "Buy milk", done: true }));
    const { result } = renderHook(() => useUpdateRow(table), { wrapper: wrapperWith(transport) });

    await act(async () => {
      await result.current.mutateAsync({ rowId: "row_1", data: { done: true } });
    });

    const patched = transport.requests.find((r) => r.method === "PATCH");
    expect(patched?.path).toBe("/v1/databases/db_1/tables/tbl_1/rows/row_1");
    expect(patched?.body).toEqual({ data: { done: true } });
  });

  it("useDeleteRow DELETEs the row", async () => {
    const transport = new FakeTransport(() => emptyResponse(204));
    const { result } = renderHook(() => useDeleteRow(table), { wrapper: wrapperWith(transport) });

    await act(async () => {
      await result.current.mutateAsync("row_1");
    });

    const deleted = transport.requests.find((r) => r.method === "DELETE");
    expect(deleted?.path).toBe("/v1/databases/db_1/tables/tbl_1/rows/row_1");
  });
});
