import { Link, useParams } from "@tanstack/react-router";
import { useMemo, useState } from "react";
import { useRedeliverWebhook, useWebhook, useWebhookDelivery, useWebhookDeliveries } from "../api/webhooks";
import type { DataGridColumn } from "../components/DataGrid";
import { DataGrid } from "../components/DataGrid";
import { Badge, EmptyState, FullPageSpinner, IdChip, Sheet, Spinner, timeAgo } from "../components/ui";
import type { WebhookDelivery, WebhookDeliveryStatus } from "../api/types";

const HEADERS = ["Time", "Event", "Status", "Attempts", "Last status", ""];

export function WebhookDeliveriesPage() {
  const { projectId, webhookId } = useParams({ strict: false }) as { projectId: string; webhookId: string };
  const webhook = useWebhook(projectId, webhookId);
  const deliveries = useWebhookDeliveries(projectId, webhookId);
  const [selectedId, setSelectedId] = useState<string | null>(null);

  const columns = useMemo<DataGridColumn<WebhookDelivery>[]>(() => [
    {
      id: "time",
      header: "Time",
      cell: ({ row }) => <span className="text-xs text-ink-400">{timeAgo(row.original.createdAt)}</span>,
    },
    {
      id: "event",
      header: "Event",
      cell: ({ row }) => <span className="font-mono text-xs text-ink-100">{row.original.eventType}</span>,
    },
    {
      id: "status",
      header: "Status",
      cell: ({ row }) => <DeliveryStatusBadge status={row.original.status} />,
    },
    {
      id: "attempts",
      header: "Attempts",
      cell: ({ row }) => <span className="text-xs text-ink-400">{row.original.attempts}</span>,
    },
    {
      id: "lastStatus",
      header: "Last status",
      cell: ({ row }) => (
        <span className="font-mono text-xs text-ink-400">{row.original.lastStatusCode ?? "—"}</span>
      ),
    },
    {
      id: "redelivered",
      header: "",
      cell: ({ row }) => (row.original.redeliveredFromId ? <Badge tone="iris">redelivery</Badge> : null),
    },
  ], []);

  if (webhook.isPending || deliveries.isPending) return <FullPageSpinner />;
  if (webhook.isError) throw webhook.error;
  if (deliveries.isError) throw deliveries.error;

  return (
    <div>
      <Link
        to="/project/$projectId/webhooks"
        params={{ projectId }}
        className="btn-ghost mb-4 justify-start px-0 text-xs text-ink-500"
      >
        ← Webhooks
      </Link>
      <div className="mb-1 flex items-center gap-3">
        <h1 className="text-2xl font-semibold tracking-tight">{webhook.data.name}</h1>
        {webhook.data.enabled ? <Badge tone="mint">enabled</Badge> : <Badge tone="coral">disabled</Badge>}
      </div>
      <p className="mb-1 font-mono text-xs text-ink-500">{webhook.data.url}</p>
      <div className="mb-6"><IdChip id={webhook.data.id} /></div>

      {!webhook.data.enabled && webhook.data.disabledReason ? (
        <div className="mb-4 rounded-lg border border-amber-400/30 bg-amber-400/10 px-3 py-2 text-sm text-amber-400">
          {webhook.data.disabledReason}
        </div>
      ) : null}

      {deliveries.data.total === 0 ? (
        <EmptyState headers={HEADERS} title="No deliveries yet. They appear here as matching events are dispatched." />
      ) : (
        <DataGrid
          columns={columns}
          data={deliveries.data.deliveries}
          getRowId={(row) => row.id}
          onRowClick={(row) => setSelectedId(row.id)}
          maxHeight="65vh"
        />
      )}

      {selectedId ? (
        <DeliverySheet
          projectId={projectId}
          webhookId={webhookId}
          deliveryId={selectedId}
          onClose={() => setSelectedId(null)}
        />
      ) : null}
    </div>
  );
}

function DeliveryStatusBadge({ status }: { status: WebhookDeliveryStatus }) {
  if (status === "succeeded") return <Badge tone="mint">succeeded</Badge>;
  if (status === "failed") return <Badge tone="coral">failed</Badge>;
  if (status === "delivering") return <Badge tone="amber">delivering</Badge>;
  return <Badge tone="ink">queued</Badge>;
}

function DeliverySheet({
  projectId,
  webhookId,
  deliveryId,
  onClose,
}: {
  projectId: string;
  webhookId: string;
  deliveryId: string;
  onClose: () => void;
}) {
  const detail = useWebhookDelivery(projectId, webhookId, deliveryId);
  const redeliver = useRedeliverWebhook(projectId, webhookId);

  if (detail.isPending) {
    return (
      <Sheet onClose={onClose} title="Delivery">
        <div className="grid place-items-center py-10"><Spinner /></div>
      </Sheet>
    );
  }
  if (detail.isError) throw detail.error;

  const { delivery, payload, attempts } = detail.data;
  const canRedeliver = delivery.status === "succeeded" || delivery.status === "failed";

  return (
    <Sheet
      onClose={onClose}
      title="Delivery"
      footer={
        <button
          type="button"
          className="btn-primary w-full"
          disabled={!canRedeliver || redeliver.isPending}
          onClick={() => redeliver.mutate(deliveryId)}
        >
          {redeliver.isPending ? <Spinner /> : redeliver.isSuccess ? "Redelivered — check the list" : "Redeliver"}
        </button>
      }
    >
      <div className="space-y-4 text-sm">
        <div className="flex items-center gap-2">
          <DeliveryStatusBadge status={delivery.status} />
          <span className="font-mono text-xs text-ink-400">{delivery.eventType}</span>
        </div>

        <div>
          <span className="mb-1 block text-xs font-medium uppercase tracking-wide text-ink-500">Payload</span>
          <pre className="overflow-x-auto rounded-lg border border-ink-700 bg-ink-950 px-4 py-3 font-mono text-xs text-ink-300">
            {JSON.stringify(payload, null, 2)}
          </pre>
        </div>

        <div>
          <span className="mb-1 block text-xs font-medium uppercase tracking-wide text-ink-500">
            Attempts ({attempts.length})
          </span>
          <div className="space-y-2">
            {attempts.length === 0 ? (
              <p className="text-xs text-ink-500">Not attempted yet.</p>
            ) : (
              [...attempts].reverse().map((attempt) => (
                <div key={attempt.attemptNumber} className="rounded-lg border border-ink-800 bg-ink-900 px-3 py-2">
                  <div className="mb-1 flex items-center justify-between text-xs">
                    <span className="text-ink-300">#{attempt.attemptNumber} · {new Date(attempt.startedAt).toLocaleTimeString()}</span>
                    <span className="text-ink-500">{attempt.durationMs}ms</span>
                  </div>
                  {attempt.statusCode ? (
                    <div className="font-mono text-xs text-ink-400">HTTP {attempt.statusCode}</div>
                  ) : null}
                  {attempt.error ? (
                    <div className="mt-1 font-mono text-xs text-coral-400">{attempt.error}</div>
                  ) : null}
                  {attempt.responseBody ? (
                    <pre className="mt-1 max-h-24 overflow-auto rounded border border-ink-800 bg-ink-950 px-2 py-1 font-mono text-[11px] text-ink-500">
                      {attempt.responseBody}
                    </pre>
                  ) : null}
                </div>
              ))
            )}
          </div>
        </div>
      </div>
    </Sheet>
  );
}
