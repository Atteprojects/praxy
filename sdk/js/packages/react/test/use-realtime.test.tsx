import { tableRef } from "@praxy/core";
import { renderHook, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { PraxyProvider } from "../src/provider";
import { useConnectionState, useLiveList } from "../src/use-realtime";
import { FakeTransport, jsonResponse } from "./support/fake-transport";
import { FakeWebSocket } from "./support/fake-websocket";

interface Todo {
  title: string;
}

const table = tableRef<Todo>("db_1", "tbl_1");
const rowMeta = { $id: "row_1", $tableId: "tbl_1", $databaseId: "db_1", $createdAt: "t", $updatedAt: "t", $permissions: [] };

function wrapperWith(transport: FakeTransport) {
  return function Wrapper({ children }: { children: ReactNode }) {
    return (
      <PraxyProvider config={{ endpoint: "https://api.test", projectId: "proj_1" }} initialJwt="jwt-1" transport={transport}>
        {children}
      </PraxyProvider>
    );
  };
}

describe("realtime hooks", () => {
  beforeEach(() => {
    FakeWebSocket.reset();
    vi.stubGlobal("WebSocket", FakeWebSocket);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it("useConnectionState tracks the shared socket's lifecycle", async () => {
    const transport = new FakeTransport((req) =>
      req.path === "/v1/realtime/ticket"
        ? jsonResponse(200, { ticket: "tkt_1", expiresAt: "2099-01-01T00:00:00Z" })
        : jsonResponse(200, { total: 0, rows: [] }),
    );
    const { result } = renderHook(
      () => ({ connection: useConnectionState(), live: useLiveList(table) }),
      { wrapper: wrapperWith(transport) },
    );

    // `useLiveList` opens the shared socket as soon as it mounts, so by the time effects have
    // flushed the state may already have moved past "disconnected" — assert the sequence lands on
    // "connected", not that we catch every earlier transition.
    await waitFor(() => expect(result.current.connection).toBe("connecting"));

    await vi.waitFor(() => expect(FakeWebSocket.instances.length).toBe(1));
    FakeWebSocket.latest().simulateOpen();
    await waitFor(() => expect(result.current.connection).toBe("connected"));
  });

  it("useLiveList seeds from the REST snapshot, then patches on a row-change event", async () => {
    const transport = new FakeTransport((req) => {
      if (req.path === "/v1/realtime/ticket") return jsonResponse(200, { ticket: "tkt_1", expiresAt: "2099-01-01T00:00:00Z" });
      if (req.path === "/v1/databases/db_1/tables/tbl_1/rows/row_2") {
        return jsonResponse(200, { ...rowMeta, $id: "row_2", title: "New row" });
      }
      return jsonResponse(200, { total: 1, rows: [{ ...rowMeta, title: "Buy milk" }] });
    });

    const { result } = renderHook(() => useLiveList(table), { wrapper: wrapperWith(transport) });

    await waitFor(() => expect(result.current.rows).toHaveLength(1));
    expect(result.current.rows[0]?.title).toBe("Buy milk");
    expect(result.current.total).toBe(1);

    await vi.waitFor(() => expect(FakeWebSocket.instances.length).toBe(1));
    const socket = FakeWebSocket.latest();
    socket.simulateOpen();

    socket.simulateMessage({
      type: "event",
      data: {
        events: ["databases.db_1.tables.tbl_1.rows.row_2.create"],
        channels: ["databases.db_1.tables.tbl_1.rows"],
        payload: { rowId: "row_2" },
      },
    });

    await waitFor(() => expect(result.current.rows).toHaveLength(2));
    expect(result.current.rows.find((r) => r.$id === "row_2")?.title).toBe("New row");
    // An exact live count would need re-running the count query on every event.
    expect(result.current.total).toBeNull();
  });
});
