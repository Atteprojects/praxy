/**
 * The shared, multiplexed WebSocket connection — one per `RealtimeService`, ref-counted across
 * every `subscribeChannel()` caller. Mirrors `praxy_flutter`'s `RealtimeSocket`
 * (`sdk/flutter/praxy_flutter/lib/src/realtime/realtime_socket.dart`): constructing more than one
 * per client is the exact bug this type exists to make structurally impossible.
 *
 * Uses the global `WebSocket` (available in browsers, edge runtimes, and Node 22+) — no
 * `node:*` import, so this stays safe to pull into `praxyMiddleware()`'s Edge Runtime bundle.
 */

export type ConnectionState = "disconnected" | "connecting" | "connected" | "reconnecting";

export interface ServerFrame {
  type: string;
  data?: unknown;
}

export interface Unsubscribe {
  unsubscribe(): void;
}

export interface RealtimeSocketConfig {
  /** `wss://host/v1/realtime?project=<id>` — a ticket is appended as `&ticket=...` per connect attempt. */
  wsUrl: string;
  /** Mints a fresh single-use ticket; `null` connects as a guest (no session/JWT to mint from). */
  mintTicket: () => Promise<string | null>;
}

const PING_INTERVAL_MS = 20_000;
const IDLE_CLOSE_MS = 5_000;
const BASE_BACKOFF_MS = 500;
const MAX_BACKOFF_MS = 30_000;
const MAX_BACKOFF_ATTEMPT = 10;

export class RealtimeSocket {
  private ws: WebSocket | null = null;
  private state: ConnectionState = "disconnected";
  private attempt = 0;
  private explicitlyClosed = false;

  private reconnectTimer: ReturnType<typeof setTimeout> | null = null;
  private pingTimer: ReturnType<typeof setInterval> | null = null;
  private idleTimer: ReturnType<typeof setTimeout> | null = null;

  private readonly channelListeners = new Map<string, Set<(frame: ServerFrame) => void>>();
  private readonly subscriptionIdByChannel = new Map<string, string>();
  private readonly stateListeners = new Set<(state: ConnectionState) => void>();
  private nextSubscriptionId = 1;

  constructor(private readonly config: RealtimeSocketConfig) {}

  subscribeChannel(channel: string, listener: (frame: ServerFrame) => void): Unsubscribe {
    this.explicitlyClosed = false;
    this.cancelIdleClose();

    let listeners = this.channelListeners.get(channel);
    const isNewChannel = !listeners;
    if (!listeners) {
      listeners = new Set();
      this.channelListeners.set(channel, listeners);
    }
    listeners.add(listener);

    if (isNewChannel && this.state === "connected") this.sendSubscribe(channel);
    this.ensureConnecting();

    return {
      unsubscribe: () => {
        listeners!.delete(listener);
        if (listeners!.size === 0) {
          this.channelListeners.delete(channel);
          if (this.state === "connected") this.sendUnsubscribe(channel);
          else this.subscriptionIdByChannel.delete(channel);
          this.scheduleIdleCloseIfEmpty();
        }
      },
    };
  }

  subscribeConnectionState(listener: (state: ConnectionState) => void): Unsubscribe {
    this.stateListeners.add(listener);
    listener(this.state);
    return { unsubscribe: () => this.stateListeners.delete(listener) };
  }

  close(): void {
    this.explicitlyClosed = true;
    this.teardown();
    this.setState("disconnected");
  }

  private ensureConnecting(): void {
    if (this.ws || this.state === "connecting" || this.state === "reconnecting") return;
    this.attempt = 0;
    void this.connect();
  }

  private async connect(): Promise<void> {
    this.setState(this.attempt > 0 ? "reconnecting" : "connecting");

    const ticket = await this.config.mintTicket().catch(() => null);
    const url = ticket ? `${this.config.wsUrl}&ticket=${encodeURIComponent(ticket)}` : this.config.wsUrl;

    let socket: WebSocket;
    try {
      socket = new WebSocket(url);
    } catch {
      this.scheduleReconnect();
      return;
    }
    this.ws = socket;

    socket.addEventListener("message", (event) => this.handleMessage(String((event as MessageEvent).data)));
    socket.addEventListener("close", () => this.handleClose());
    socket.addEventListener("error", () => {
      /* the subsequent close event drives reconnect — nothing extra to do here */
    });
  }

  private handleMessage(raw: string): void {
    let frame: ServerFrame;
    try {
      frame = JSON.parse(raw) as ServerFrame;
    } catch {
      return;
    }

    if (frame.type === "connected") {
      this.attempt = 0;
      this.setState("connected");
      this.resubscribeAll();
      this.startPing();
      return;
    }
    if (frame.type === "pong") return;
    if (frame.type === "event") {
      const channels = (frame.data as { channels?: string[] } | undefined)?.channels ?? [];
      for (const channel of channels) {
        this.channelListeners.get(channel)?.forEach((listener) => listener(frame));
      }
    }
  }

  private handleClose(): void {
    this.ws = null;
    this.stopPing();
    this.subscriptionIdByChannel.clear();
    if (this.explicitlyClosed) {
      this.setState("disconnected");
      return;
    }
    this.setState("disconnected");
    if (this.channelListeners.size > 0) this.scheduleReconnect();
  }

  /** Exponential backoff with full jitter — avoids every disconnected client retrying in lockstep. */
  private scheduleReconnect(): void {
    if (this.explicitlyClosed) return;
    const cappedAttempt = Math.min(this.attempt, MAX_BACKOFF_ATTEMPT);
    const cap = Math.min(MAX_BACKOFF_MS, BASE_BACKOFF_MS * 2 ** cappedAttempt);
    const delay = Math.random() * cap;
    this.attempt += 1;
    this.reconnectTimer = setTimeout(() => {
      this.reconnectTimer = null;
      if (this.channelListeners.size > 0) void this.connect();
    }, delay);
  }

  private resubscribeAll(): void {
    for (const channel of this.channelListeners.keys()) this.sendSubscribe(channel);
  }

  private sendSubscribe(channel: string): void {
    const subscriptionId = `s${this.nextSubscriptionId++}`;
    this.subscriptionIdByChannel.set(channel, subscriptionId);
    this.send({ type: "subscribe", data: [{ subscriptionId, channels: [channel] }] });
  }

  private sendUnsubscribe(channel: string): void {
    const subscriptionId = this.subscriptionIdByChannel.get(channel);
    this.subscriptionIdByChannel.delete(channel);
    if (subscriptionId) this.send({ type: "unsubscribe", data: [{ subscriptionId }] });
  }

  private send(message: unknown): void {
    if (this.ws?.readyState === WebSocket.OPEN) this.ws.send(JSON.stringify(message));
  }

  private startPing(): void {
    this.stopPing();
    this.pingTimer = setInterval(() => this.send({ type: "ping" }), PING_INTERVAL_MS);
  }

  private stopPing(): void {
    if (this.pingTimer) clearInterval(this.pingTimer);
    this.pingTimer = null;
  }

  /** Closes the socket once the last subscription cancels, after a short grace period — avoids
   *  thrashing the connection across React's rapid mount/unmount cycles (e.g. StrictMode). */
  private scheduleIdleCloseIfEmpty(): void {
    if (this.channelListeners.size > 0) return;
    this.cancelIdleClose();
    this.idleTimer = setTimeout(() => {
      if (this.channelListeners.size === 0) this.teardown();
    }, IDLE_CLOSE_MS);
  }

  private cancelIdleClose(): void {
    if (this.idleTimer) clearTimeout(this.idleTimer);
    this.idleTimer = null;
  }

  private teardown(): void {
    this.cancelIdleClose();
    if (this.reconnectTimer) clearTimeout(this.reconnectTimer);
    this.reconnectTimer = null;
    this.stopPing();
    this.ws?.close();
    this.ws = null;
  }

  private setState(state: ConnectionState): void {
    if (this.state === state) return;
    this.state = state;
    this.stateListeners.forEach((listener) => listener(state));
  }
}
