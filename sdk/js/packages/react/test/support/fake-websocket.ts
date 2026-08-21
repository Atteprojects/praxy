type Listener = (event: { data?: string }) => void;

/** Mirrors `@praxy/core`'s own `test/support/fake-websocket.ts` — duplicated locally, same reason as `fake-transport.ts`. */
export class FakeWebSocket {
  static readonly CONNECTING = 0;
  static readonly OPEN = 1;
  static readonly CLOSING = 2;
  static readonly CLOSED = 3;
  static instances: FakeWebSocket[] = [];

  readyState = FakeWebSocket.CONNECTING;
  readonly sent: string[] = [];
  private readonly listeners: Record<string, Listener[]> = {};

  constructor(readonly url: string) {
    FakeWebSocket.instances.push(this);
  }

  addEventListener(type: string, listener: Listener): void {
    (this.listeners[type] ??= []).push(listener);
  }

  send(data: string): void {
    this.sent.push(data);
  }

  close(): void {
    this.readyState = FakeWebSocket.CLOSED;
    this.emit("close", {});
  }

  simulateOpen(): void {
    this.readyState = FakeWebSocket.OPEN;
    this.emit("message", { data: JSON.stringify({ type: "connected", data: { user: null, channels: [] } }) });
  }

  simulateMessage(frame: unknown): void {
    this.emit("message", { data: JSON.stringify(frame) });
  }

  emit(type: string, event: { data?: string }): void {
    this.listeners[type]?.forEach((listener) => listener(event));
  }

  static reset(): void {
    FakeWebSocket.instances = [];
  }

  static latest(): FakeWebSocket {
    const instance = FakeWebSocket.instances.at(-1);
    if (!instance) throw new Error("No FakeWebSocket has been constructed yet.");
    return instance;
  }
}
