import type { Praxy } from "../client.js";
import type { ConnectionState, ServerFrame, Unsubscribe } from "../realtime-socket.js";
import { RealtimeSocket } from "../realtime-socket.js";
import type { TableRef } from "../table-ref.js";

export type { ConnectionState, Unsubscribe } from "../realtime-socket.js";

export interface RowChangeEvent {
  action: "create" | "update" | "delete";
  rowId: string;
}

/**
 * A generic account/session change notification. The exact per-action grammar on this channel
 * isn't pinned down the way the row-event grammar is (`databases.{db}.tables.{tbl}.rows.{id}.{action}`)
 * — `event` carries the server's raw dot-path so a caller can still switch on it, and `payload`
 * is passed through unparsed.
 */
export interface AccountChangeEvent {
  event: string;
  payload: unknown;
}

/**
 * Client-side realtime — a Server Component can't hold a WebSocket across a request/response
 * cycle, so this only ever runs in the browser (see `@praxy/react`'s `useLiveList`). Authenticates
 * with the same JWT bridge as `@praxy/react`'s REST calls: `Praxy.mintRealtimeTicket()` exchanges
 * whatever credential this client holds for a single-use WS ticket, since the browser's native
 * `WebSocket` can't set the `X-Praxy-Session`/`X-Praxy-Key` headers a plain request would.
 *
 * 4 methods, matching `praxy_flutter`'s `PraxyRealtime`
 * (`sdk/flutter/praxy_flutter/lib/src/realtime/realtime.dart`): `rows`, `account`, `connection`, `close`.
 */
export class RealtimeService {
  private socket: RealtimeSocket | null = null;

  constructor(private readonly client: Praxy) {}

  /**
   * Subscribes to row changes on a table (or one row, if `rowId` is given). Delivers only
   * `{action, rowId}` — the server's row-change event never carries column data — so a caller
   * (typically `@praxy/react`'s `useLiveList`) re-fetches via `tables.get`/`tables.list` on each event.
   */
  rows<T>(table: TableRef<T>, listener: (event: RowChangeEvent) => void, options: { rowId?: string } = {}): Unsubscribe {
    const channel = options.rowId
      ? `databases.${table.databaseId}.tables.${table.tableId}.rows.${options.rowId}`
      : `databases.${table.databaseId}.tables.${table.tableId}.rows`;

    const pattern = new RegExp(
      `^databases\\.${escapeRegExp(table.databaseId)}\\.tables\\.${escapeRegExp(table.tableId)}\\.rows\\.([^.]+)\\.(create|update|delete)$`,
    );

    return this.ensureSocket().subscribeChannel(channel, (frame) => {
      const change = parseRowChange(frame, pattern);
      if (change) listener(change);
    });
  }

  /** Subscribes to the caller's own account/session events (server-rewritten to `account.<userId>`). */
  account(listener: (event: AccountChangeEvent) => void): Unsubscribe {
    return this.ensureSocket().subscribeChannel("account", (frame) => {
      const data = frame.data as { events?: string[]; payload?: unknown } | undefined;
      const event = data?.events?.[0];
      if (event) listener({ event, payload: data?.payload });
    });
  }

  /** The shared socket's own connection lifecycle — replays the current state to a new listener immediately. */
  connection(listener: (state: ConnectionState) => void): Unsubscribe {
    return this.ensureSocket().subscribeConnectionState(listener);
  }

  /** Disposes the shared socket. A later `rows`/`account`/`connection` call lazily reopens one. */
  close(): void {
    this.socket?.close();
  }

  private ensureSocket(): RealtimeSocket {
    if (!this.socket) {
      this.socket = new RealtimeSocket({
        wsUrl: toWebSocketUrl(this.client.endpoint, this.client.projectId),
        mintTicket: async () => {
          try {
            return (await this.client.mintRealtimeTicket()).ticket;
          } catch {
            return null; // fall back to an unauthenticated (guest-role) connection
          }
        },
      });
    }
    return this.socket;
  }
}

function parseRowChange(frame: ServerFrame, pattern: RegExp): RowChangeEvent | null {
  const events = (frame.data as { events?: string[] } | undefined)?.events ?? [];
  for (const event of events) {
    const match = pattern.exec(event);
    if (match) return { rowId: match[1]!, action: match[2] as RowChangeEvent["action"] };
  }
  return null;
}

function toWebSocketUrl(endpoint: string, projectId: string): string {
  const url = new URL(endpoint);
  url.protocol = url.protocol === "https:" ? "wss:" : "ws:";
  url.pathname = url.pathname.replace(/\/+$/, "") + "/v1/realtime";
  url.search = `?project=${encodeURIComponent(projectId)}`;
  return url.toString();
}

function escapeRegExp(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}
