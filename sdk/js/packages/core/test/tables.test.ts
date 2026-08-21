import { describe, expect, it } from "vitest";
import { Praxy } from "../src/client";
import { Col, Query } from "../src/query";
import { tableRef } from "../src/table-ref";
import type { TransportRequest } from "../src/transport";
import { emptyResponse, FakeTransport, jsonResponse } from "./support/fake-transport";

interface Todo {
  title: string;
  done: boolean;
}

const TodoTitle = new Col<string>("title");

function clientCapturing(response: ReturnType<typeof jsonResponse>) {
  let captured!: TransportRequest;
  const transport = new FakeTransport((req) => {
    captured = req;
    return response;
  });
  const client = new Praxy({ endpoint: "https://api.test", projectId: "proj_1", transport });
  return { client, captured: () => captured };
}

const rowMeta = { $id: "row_1", $tableId: "tbl_1", $databaseId: "db_1", $createdAt: "t", $updatedAt: "t", $permissions: [] };

describe("TablesService", () => {
  const table = tableRef<Todo>("db_1", "tbl_1");

  it("list() → GET .../rows with no query params by default", async () => {
    const { client, captured } = clientCapturing(jsonResponse(200, { total: 0, rows: [] }));
    await client.tables.list(table);
    expect(captured().method).toBe("GET");
    expect(captured().path).toBe("/v1/databases/db_1/tables/tbl_1/rows");
    expect(captured().query).toEqual({});
  });

  it("list() encodes queries as repeated queries[] entries", async () => {
    const { client, captured } = clientCapturing(jsonResponse(200, { total: 1, rows: [] }));
    await client.tables.list(table, { queries: [Query.equal(TodoTitle, "Buy milk"), Query.limit(10)] });
    expect(captured().query).toEqual({
      "queries[]": [
        JSON.stringify({ method: "equal", attribute: "title", values: ["Buy milk"] }),
        JSON.stringify({ method: "limit", values: [10] }),
      ],
    });
  });

  it("list({ total: false }) sets total=false", async () => {
    const { client, captured } = clientCapturing(jsonResponse(200, { total: null, rows: [] }));
    await client.tables.list(table, { total: false });
    expect(captured().query).toEqual({ total: ["false"] });
  });

  it("get() → GET .../rows/{id}", async () => {
    const { client, captured } = clientCapturing(jsonResponse(200, { ...rowMeta, title: "Buy milk", done: false }));
    const row = await client.tables.get(table, "row_1");
    expect(captured().path).toBe("/v1/databases/db_1/tables/tbl_1/rows/row_1");
    expect(row.title).toBe("Buy milk");
    expect(row.$id).toBe("row_1");
  });

  it("create() → POST .../rows with {rowId?, data, permissions?}", async () => {
    const { client, captured } = clientCapturing(jsonResponse(201, { ...rowMeta, title: "Buy milk", done: false }));
    await client.tables.create(table, { data: { title: "Buy milk", done: false }, permissions: ["read(\"any\")"] });
    expect(captured().method).toBe("POST");
    expect(captured().body).toEqual({ data: { title: "Buy milk", done: false }, permissions: ["read(\"any\")"] });
  });

  it("update() sends only the changed fields under data", async () => {
    const { client, captured } = clientCapturing(jsonResponse(200, { ...rowMeta, title: "Buy milk", done: true }));
    await client.tables.update(table, "row_1", { data: { done: true } });
    expect(captured().method).toBe("PATCH");
    expect(captured().path).toBe("/v1/databases/db_1/tables/tbl_1/rows/row_1");
    expect(captured().body).toEqual({ data: { done: true } });
  });

  it("delete() → DELETE .../rows/{id}", async () => {
    const { client, captured } = clientCapturing(emptyResponse(204));
    await client.tables.delete(table, "row_1");
    expect(captured().method).toBe("DELETE");
    expect(captured().path).toBe("/v1/databases/db_1/tables/tbl_1/rows/row_1");
  });
});
