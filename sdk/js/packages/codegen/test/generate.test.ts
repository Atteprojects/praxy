import { describe, expect, it } from "vitest";
import { CodegenError, generate } from "../src/generate";

function fakeFetch(routes: Record<string, unknown>): typeof fetch {
  return (async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input));
    const body = routes[url.pathname];
    if (body === undefined) {
      return new Response("not found", { status: 404 });
    }
    expect(new Headers(init?.headers).get("x-praxy-key")).toBe("key_1");
    expect(new Headers(init?.headers).get("x-praxy-project")).toBe("proj_1");
    return new Response(JSON.stringify(body), { status: 200 });
  }) as typeof fetch;
}

const baseOptions = {
  endpoint: "https://api.test",
  projectId: "proj_1",
  apiKey: "key_1",
  database: "main",
  table: "todos",
};

describe("generate", () => {
  it("resolves database/table by key, then renders the interface + Col constants", async () => {
    const fetchImpl = fakeFetch({
      "/v1/databases": { databases: [{ id: "db_1", key: "main" }] },
      "/v1/databases/db_1/tables": { tables: [{ id: "tbl_1", key: "todos" }] },
      "/v1/databases/db_1/tables/tbl_1/columns": {
        columns: [
          { key: "title", type: "string", array: false, required: true },
          { key: "tags", type: "string", array: true, required: false },
          { key: "priority", type: "integer", array: false, required: false },
        ],
      },
    });

    const code = await generate(baseOptions, fetchImpl);

    expect(code).toContain('import { Col } from "@praxy/core";');
    expect(code).toContain("export interface Todos {");
    expect(code).toContain("title: string;");
    expect(code).toContain("tags?: string[];");
    expect(code).toContain("priority?: number;");
    expect(code).toContain('export const TodosColumns = {');
    expect(code).toContain('id: new Col<string>("$id"),');
    expect(code).toContain('title: new Col<string>("title"),');
    expect(code).toContain('tags: new Col<string[]>("tags"),');
  });

  it("uses --class-name when given, instead of a PascalCase table key", async () => {
    const fetchImpl = fakeFetch({
      "/v1/databases": { databases: [{ id: "db_1", key: "main" }] },
      "/v1/databases/db_1/tables": { tables: [{ id: "tbl_1", key: "todos" }] },
      "/v1/databases/db_1/tables/tbl_1/columns": { columns: [] },
    });

    const code = await generate({ ...baseOptions, className: "Task" }, fetchImpl);
    expect(code).toContain("export interface Task {");
    expect(code).toContain("export const TaskColumns = {");
  });

  it("quotes a column key that isn't a valid identifier", async () => {
    const fetchImpl = fakeFetch({
      "/v1/databases": { databases: [{ id: "db_1", key: "main" }] },
      "/v1/databases/db_1/tables": { tables: [{ id: "tbl_1", key: "todos" }] },
      "/v1/databases/db_1/tables/tbl_1/columns": {
        columns: [{ key: "due-date", type: "datetime", array: false, required: false }],
      },
    });

    const code = await generate(baseOptions, fetchImpl);
    expect(code).toContain('"due-date"?: string;');
    expect(code).toContain('"due-date": new Col<string>("due-date"),');
  });

  it("throws when no database/table matches the given key", async () => {
    const fetchImpl = fakeFetch({ "/v1/databases": { databases: [{ id: "db_1", key: "other" }] } });
    await expect(generate(baseOptions, fetchImpl)).rejects.toThrow(/No database with key 'main'/);
  });

  it("throws CodegenError with path/status/body on a non-2xx response", async () => {
    const fetchImpl = (async () => new Response("nope", { status: 401 })) as typeof fetch;
    const error = await generate(baseOptions, fetchImpl).catch((e) => e);
    expect(error).toBeInstanceOf(CodegenError);
    expect((error as CodegenError).status).toBe(401);
    expect((error as CodegenError).path).toBe("/v1/databases");
  });
});
