import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { Praxy } from "../src/client";
import { tableRef } from "../src/table-ref";
import { FakeTransport, jsonResponse } from "./support/fake-transport";
import { FakeWebSocket } from "./support/fake-websocket";

interface Todo {
  title: string;
}

const table = tableRef<Todo>("db_1", "tbl_1");

function makeClient(sessionToken?: string) {
  // Mirrors the real ticket-mint endpoint's own auth gate: only a request carrying a credential
  // header gets a ticket back — an anonymous request 401s, same as any other authenticated route.
  const transport = new FakeTransport((req) =>
    req.headers?.["X-Praxy-Session"]
      ? jsonResponse(200, { ticket: "tkt_1", expiresAt: "2099-01-01T00:00:00Z" })
      : jsonResponse(401, { message: "Unauthorized.", code: 401, type: "general_unauthorized", version: "v1", requestId: "r1" }),
  );
  const client = new Praxy({ endpoint: "https://api.test", projectId: "proj_1", sessionToken, transport });
  return { client, transport };
}

describe("RealtimeService", () => {
  beforeEach(() => {
    FakeWebSocket.reset();
    vi.stubGlobal("WebSocket", FakeWebSocket);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it("connects to wss://<host>/v1/realtime?project=<id> and appends a minted ticket", async () => {
    const { client } = makeClient("secret-session");
    const sub = client.realtime.rows(table, () => {});

    await vi.waitFor(() => expect(FakeWebSocket.instances.length).toBe(1));
    await vi.waitFor(() => expect(FakeWebSocket.latest().url).toContain("ticket=tkt_1"));
    expect(FakeWebSocket.latest().url).toBe("wss://api.test/v1/realtime?project=proj_1&ticket=tkt_1");

    sub.unsubscribe();
    client.realtime.close();
  });

  it("sends a subscribe message once connected, and delivers row create/update/delete events", async () => {
    const { client } = makeClient("secret-session");
    const events: Array<{ action: string; rowId: string }> = [];
    client.realtime.rows(table, (event) => events.push(event));

    await vi.waitFor(() => expect(FakeWebSocket.instances.length).toBe(1));
    const socket = FakeWebSocket.latest();
    socket.simulateOpen();

    await vi.waitFor(() => {
      const subscribeMsg = socket.sent.map((s) => JSON.parse(s)).find((m) => m.type === "subscribe");
      expect(subscribeMsg?.data[0]?.channels).toEqual(["databases.db_1.tables.tbl_1.rows"]);
    });

    socket.simulateMessage({
      type: "event",
      data: {
        events: ["databases.db_1.tables.tbl_1.rows.row_1.create"],
        channels: ["databases.db_1.tables.tbl_1.rows"],
        subscriptions: ["s1"],
        payload: { rowId: "row_1" },
      },
    });
    socket.simulateMessage({
      type: "event",
      data: {
        events: ["databases.db_1.tables.tbl_1.rows.row_1.delete"],
        channels: ["databases.db_1.tables.tbl_1.rows"],
        subscriptions: ["s1"],
        payload: { rowId: "row_1" },
      },
    });

    expect(events).toEqual([
      { action: "create", rowId: "row_1" },
      { action: "delete", rowId: "row_1" },
    ]);
    client.realtime.close();
  });

  it("scopes the channel to one row when rowId is given", async () => {
    const { client } = makeClient("secret-session");
    client.realtime.rows(table, () => {}, { rowId: "row_1" });

    await vi.waitFor(() => expect(FakeWebSocket.instances.length).toBe(1));
    const socket = FakeWebSocket.latest();
    socket.simulateOpen();

    await vi.waitFor(() => {
      const subscribeMsg = socket.sent.map((s) => JSON.parse(s)).find((m) => m.type === "subscribe");
      expect(subscribeMsg?.data[0]?.channels).toEqual(["databases.db_1.tables.tbl_1.rows.row_1"]);
    });
    client.realtime.close();
  });

  it("delivers account events with the raw event path", async () => {
    const { client } = makeClient("secret-session");
    const events: Array<{ event: string; payload: unknown }> = [];
    client.realtime.account((event) => events.push(event));

    await vi.waitFor(() => expect(FakeWebSocket.instances.length).toBe(1));
    const socket = FakeWebSocket.latest();
    socket.simulateOpen();
    await vi.waitFor(() => expect(socket.sent.length).toBeGreaterThan(0));

    socket.simulateMessage({
      type: "event",
      data: { events: ["account.u1.session.create"], channels: ["account"], payload: { userId: "u1" } },
    });

    expect(events).toEqual([{ event: "account.u1.session.create", payload: { userId: "u1" } }]);
    client.realtime.close();
  });

  it("connects without a ticket (guest) when the client holds no credential", async () => {
    const { client } = makeClient(undefined);
    client.realtime.rows(table, () => {});
    await vi.waitFor(() => expect(FakeWebSocket.instances.length).toBe(1));
    expect(FakeWebSocket.latest().url).toBe("wss://api.test/v1/realtime?project=proj_1");
    client.realtime.close();
  });

  it("connection() replays the current state immediately, then reports connecting → connected", async () => {
    const { client } = makeClient("secret-session");
    const states: string[] = [];
    client.realtime.connection((state) => states.push(state));
    // subscribing to `connection` alone doesn't open a socket — only rows()/account() do.
    expect(states).toEqual(["disconnected"]);

    client.realtime.rows(table, () => {});
    await vi.waitFor(() => expect(states).toContain("connecting"));
    await vi.waitFor(() => expect(FakeWebSocket.instances.length).toBe(1));

    FakeWebSocket.latest().simulateOpen();
    await vi.waitFor(() => expect(states).toContain("connected"));
    client.realtime.close();
  });

  it("close() tears down the socket", async () => {
    const { client } = makeClient("secret-session");
    client.realtime.rows(table, () => {});
    await vi.waitFor(() => expect(FakeWebSocket.instances.length).toBe(1));
    FakeWebSocket.latest().simulateOpen();

    client.realtime.close();
    expect(FakeWebSocket.latest().readyState).toBe(FakeWebSocket.CLOSED);
  });
});
