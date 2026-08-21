import { AccountService } from "./services/account.js";
import { FunctionsService } from "./services/functions.js";
import { RealtimeService } from "./services/realtime.js";
import { TablesService } from "./services/tables.js";
import { TeamsService } from "./services/teams.js";
import type { ErrorEnvelope } from "./errors.js";
import { PraxyDecodeError, PraxyNetworkError, mapApiError } from "./errors.js";
import type { RealtimeTicket } from "./models.js";
import { FetchTransport } from "./transport.js";
import type { Transport } from "./transport.js";

export interface PraxyConfig {
  endpoint: string;
  projectId: string;
  /** A full session secret or a scoped JWT — the server tells them apart by dot count. Never both with `apiKey`. */
  sessionToken?: string;
  /** Server-only: a project API key. Never ship this to a browser bundle. */
  apiKey?: string;
  transport?: Transport;
}

export interface RequestOptions {
  query?: Record<string, string[]>;
  body?: unknown;
}

/**
 * The isomorphic Praxy client. Construct a fresh instance per request in any server context
 * (`@praxy/nextjs`'s `createServerClient()`/`createApiKeyClient()` do exactly this) — never hoist
 * one to module scope, since a config carrying a session token would then leak across requests.
 */
export class Praxy {
  readonly account: AccountService;
  readonly tables: TablesService;
  readonly teams: TeamsService;
  readonly functions: FunctionsService;
  readonly realtime: RealtimeService;

  readonly endpoint: string;
  readonly projectId: string;
  private readonly sessionToken?: string;
  private readonly apiKey?: string;
  private readonly transport: Transport;

  constructor(config: PraxyConfig) {
    this.endpoint = config.endpoint;
    this.projectId = config.projectId;
    this.sessionToken = config.sessionToken;
    this.apiKey = config.apiKey;
    this.transport = config.transport ?? new FetchTransport({ endpoint: config.endpoint });

    this.account = new AccountService(this);
    this.tables = new TablesService(this);
    this.teams = new TeamsService(this);
    this.functions = new FunctionsService(this);
    this.realtime = new RealtimeService(this);
  }

  /** The credential this client authenticates with, for callers that need to forward it (e.g. minting a realtime ticket). */
  get authHeader(): { name: string; value: string } | undefined {
    if (this.apiKey) return { name: "X-Praxy-Key", value: this.apiKey };
    if (this.sessionToken) return { name: "X-Praxy-Session", value: this.sessionToken };
    return undefined;
  }

  /** Internal — used by the service classes. Not part of the public surface. */
  async request<T>(method: string, path: string, options: RequestOptions = {}): Promise<T> {
    const headers: Record<string, string> = {
      accept: "application/json",
      "X-Praxy-Project": this.projectId,
    };
    const auth = this.authHeader;
    if (auth) headers[auth.name] = auth.value;

    let response;
    try {
      response = await this.transport.send({
        method,
        path,
        headers,
        query: options.query,
        body: options.body,
      });
    } catch (cause) {
      throw new PraxyNetworkError("Network request failed.", cause);
    }

    if (response.status >= 200 && response.status < 300) {
      if (response.status === 204 || response.body.length === 0) {
        return undefined as T;
      }
      try {
        return JSON.parse(response.body) as T;
      } catch {
        throw new PraxyDecodeError(`Could not parse response body from ${method} ${path} as JSON.`);
      }
    }

    let envelope: ErrorEnvelope;
    try {
      envelope = JSON.parse(response.body) as ErrorEnvelope;
    } catch {
      envelope = {
        message: `Request failed with status ${response.status}.`,
        code: response.status,
        type: "general_server_error",
        version: "",
        requestId: response.headers["x-praxy-request-id"] ?? "",
      };
    }
    throw mapApiError(response.status, envelope, response.headers);
  }

  /**
   * Mints a single-use, 60s-lived realtime ticket (`POST /v1/realtime/ticket`) — the browser's
   * native `WebSocket` can't set custom headers on the handshake, so the WS connection
   * authenticates via `?ticket=` on the URL instead of the `X-Praxy-Session`/`X-Praxy-Key`
   * headers this client's other requests use. Requires this client to already be authenticated
   * (a session token, JWT, or API key) — an anonymous client mints nothing and the socket
   * connects as a guest.
   */
  mintRealtimeTicket(): Promise<RealtimeTicket> {
    return this.request<RealtimeTicket>("POST", "/v1/realtime/ticket");
  }
}
