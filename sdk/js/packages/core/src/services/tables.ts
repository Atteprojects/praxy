import type { Praxy } from "../client.js";
import type { Row, RowList } from "../models.js";
import type { Query } from "../query.js";
import type { TableRef } from "../table-ref.js";

/**
 * Row CRUD against `/v1/databases/{db}/tables/{table}/rows`. 5 methods — no `upsert`, the same
 * real API gap `praxy_core`'s `TablesService` doc comment documents (no server route exists).
 * `liveList<T>` isn't here: realtime is exclusively a `@praxy/react` concern (Server Components
 * can't hold a WebSocket across a request/response cycle) — see `@praxy/react`'s `useLiveList`.
 */
export class TablesService {
  constructor(private readonly client: Praxy) {}

  list<T>(table: TableRef<T>, options: { queries?: Query[]; total?: boolean } = {}): Promise<RowList<T>> {
    const query: Record<string, string[]> = {};
    if (options.queries?.length) query["queries[]"] = options.queries.map((q) => q.encode());
    if (options.total === false) query.total = ["false"];
    return this.client.request<RowList<T>>("GET", this.rowsPath(table), { query });
  }

  get<T>(table: TableRef<T>, rowId: string): Promise<Row<T>> {
    return this.client.request<Row<T>>("GET", `${this.rowsPath(table)}/${encodeURIComponent(rowId)}`);
  }

  create<T>(table: TableRef<T>, input: { rowId?: string; data: T; permissions?: string[] }): Promise<Row<T>> {
    return this.client.request<Row<T>>("POST", this.rowsPath(table), { body: input });
  }

  /** Genuinely partial — only the keys present in `data` are sent, matching the server's partial-PATCH contract. */
  update<T>(
    table: TableRef<T>,
    rowId: string,
    input: { data?: Partial<T>; permissions?: string[] },
  ): Promise<Row<T>> {
    return this.client.request<Row<T>>("PATCH", `${this.rowsPath(table)}/${encodeURIComponent(rowId)}`, {
      body: input,
    });
  }

  delete<T>(table: TableRef<T>, rowId: string): Promise<void> {
    return this.client.request<void>("DELETE", `${this.rowsPath(table)}/${encodeURIComponent(rowId)}`);
  }

  private rowsPath(table: TableRef<unknown>): string {
    return `/v1/databases/${encodeURIComponent(table.databaseId)}/tables/${encodeURIComponent(table.tableId)}/rows`;
  }
}
