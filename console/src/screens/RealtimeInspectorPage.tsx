import { useParams } from "@tanstack/react-router";
import { useEffect, useMemo, useRef, useState } from "react";
import type { DataGridColumn } from "../components/DataGrid";
import { DataGrid } from "../components/DataGrid";
import { Badge, EmptyState, Sheet } from "../components/ui";
import { STR } from "../strings";

interface InspectorEvent {
  id: number;
  receivedAt: Date;
  events: string[];
  channels: string[];
  subscriptions: string[];
  timestamp: string;
  payload: unknown;
}

type Status = "connecting" | "live" | "disconnected";

const MAX_EVENTS = 200;
const FIREHOSE_SUBSCRIPTION_ID = "inspector";

/**
 * The realtime debugging tool console-design.md calls "the cheapest possible debugging tool for
 * the fan-out logic": subscribes to every `databases.*` row event the operator's bypass-everything
 * connection can see (research/appwrite-api.md's channel grammar has no wildcard subscribe for
 * ordinary callers — this firehose channel is a bypass-only extension, see ChannelGrammar/
 * ConnectionRegistry on the backend), with a channel filter and a raw payload viewer.
 */
export function RealtimeInspectorPage() {
  const { projectId } = useParams({ strict: false }) as { projectId: string };
  const [status, setStatus] = useState<Status>("connecting");
  const [events, setEvents] = useState<InspectorEvent[]>([]);
  const [filter, setFilter] = useState("");
  const [selected, setSelected] = useState<InspectorEvent | null>(null);
  const nextId = useRef(0);

  useEffect(() => {
    let cancelled = false;
    let socket: WebSocket | null = null;
    let retryTimer: ReturnType<typeof setTimeout> | null = null;

    function connect() {
      if (cancelled) return;
      setStatus("connecting");
      const proto = window.location.protocol === "https:" ? "wss:" : "ws:";
      socket = new WebSocket(`${proto}//${window.location.host}/v1/realtime?project=${projectId}`);

      socket.onopen = () => {
        socket?.send(JSON.stringify({
          type: "subscribe",
          data: [{ subscriptionId: FIREHOSE_SUBSCRIPTION_ID, channels: ["databases.*"] }],
        }));
      };

      socket.onmessage = (message: MessageEvent<string>) => {
        const parsed = JSON.parse(message.data) as { type: string; data?: Record<string, unknown> };
        if (parsed.type === "connected") {
          setStatus("live");
        } else if (parsed.type === "event" && parsed.data) {
          const data = parsed.data as {
            events: string[];
            channels: string[];
            subscriptions: string[];
            timestamp: string;
            payload: unknown;
          };
          setEvents((prev) => [
            { id: nextId.current++, receivedAt: new Date(), ...data },
            ...prev,
          ].slice(0, MAX_EVENTS));
        }
      };

      socket.onclose = () => {
        if (cancelled) return;
        setStatus("disconnected");
        retryTimer = setTimeout(connect, 3_000);
      };
    }

    connect();
    return () => {
      cancelled = true;
      if (retryTimer) clearTimeout(retryTimer);
      socket?.close();
    };
  }, [projectId]);

  const filtered = useMemo(() => {
    const needle = filter.trim().toLowerCase();
    if (!needle) return events;
    return events.filter((e) => e.channels.some((c) => c.toLowerCase().includes(needle)) ||
      e.events.some((t) => t.toLowerCase().includes(needle)));
  }, [events, filter]);

  const columns = useMemo<DataGridColumn<InspectorEvent>[]>(() => [
    {
      id: "time",
      header: "Time",
      cell: ({ row }) => (
        <span className="font-mono text-xs text-ink-500">{row.original.receivedAt.toLocaleTimeString()}</span>
      ),
    },
    {
      id: "event",
      header: "Event",
      cell: ({ row }) => <span className="font-mono text-xs text-ink-100">{row.original.events[0]}</span>,
    },
    {
      id: "channels",
      header: "Channels",
      cell: ({ row }) => (
        <span className="truncate font-mono text-xs text-ink-400">{row.original.channels.join(", ")}</span>
      ),
    },
  ], []);

  return (
    <div>
      <div className="mb-1 flex items-center gap-3">
        <h1 className="text-2xl font-semibold tracking-tight">{STR.realtime}</h1>
        <StatusBadge status={status} />
      </div>
      <p className="mb-6 text-sm text-ink-500">
        A live tail of every row event on this project, as seen through a bypass-everything
        connection — permission filtering for real subscribers happens the same way, just scoped
        to their own roles.
      </p>

      <div className="mb-4 flex flex-wrap items-center gap-2">
        <input
          type="text"
          value={filter}
          onChange={(e) => setFilter(e.target.value)}
          placeholder="Filter by channel or event name…"
          className="w-full rounded-lg border border-ink-700 bg-ink-900 px-3 py-1.5 text-sm text-ink-100 placeholder:text-ink-600 focus:border-iris-400 focus:outline-none sm:w-80"
        />
        <button type="button" className="btn-ghost border border-ink-700 text-xs" onClick={() => setEvents([])}>
          Clear
        </button>
        <span className="text-xs text-ink-500 sm:ml-auto">{filtered.length} shown · {events.length} received</span>
      </div>

      {filtered.length === 0 ? (
        <EmptyState
          headers={["Time", "Event", "Channels"]}
          title={status === "live" ? "Waiting for the first event…" : "Connecting…"}
        />
      ) : (
        <DataGrid
          columns={columns}
          data={filtered}
          getRowId={(row) => String(row.id)}
          onRowClick={setSelected}
          maxHeight="65vh"
        />
      )}

      {selected ? <PayloadSheet event={selected} onClose={() => setSelected(null)} /> : null}
    </div>
  );
}

function StatusBadge({ status }: { status: Status }) {
  if (status === "live") return <Badge tone="mint">Live</Badge>;
  if (status === "connecting") return <Badge tone="amber">Connecting…</Badge>;
  return <Badge tone="coral">Disconnected — retrying</Badge>;
}

function PayloadSheet({ event, onClose }: { event: InspectorEvent; onClose: () => void }) {
  return (
    <Sheet onClose={onClose} title="Event payload">
      <div className="space-y-4 text-sm">
        <div>
          <span className="mb-1 block text-xs font-medium uppercase tracking-wide text-ink-500">Events</span>
          <div className="space-y-1 font-mono text-xs text-ink-300">
            {event.events.map((e) => <div key={e}>{e}</div>)}
          </div>
        </div>
        <div>
          <span className="mb-1 block text-xs font-medium uppercase tracking-wide text-ink-500">Channels</span>
          <div className="space-y-1 font-mono text-xs text-ink-300">
            {event.channels.map((c) => <div key={c}>{c}</div>)}
          </div>
        </div>
        <div>
          <span className="mb-1 block text-xs font-medium uppercase tracking-wide text-ink-500">Timestamp</span>
          <div className="font-mono text-xs text-ink-300">{event.timestamp}</div>
        </div>
        <div>
          <span className="mb-1 block text-xs font-medium uppercase tracking-wide text-ink-500">Payload</span>
          <pre className="overflow-x-auto rounded-lg border border-ink-700 bg-ink-950 px-4 py-3 font-mono text-xs text-ink-300">
            {JSON.stringify(event.payload, null, 2)}
          </pre>
        </div>
      </div>
    </Sheet>
  );
}
