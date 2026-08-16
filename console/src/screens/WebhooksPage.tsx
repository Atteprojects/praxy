import { Link, useParams } from "@tanstack/react-router";
import { useState, type FormEvent } from "react";
import { useCreateWebhook, useDeleteWebhook, useUpdateWebhook, useWebhooks } from "../api/webhooks";
import { ApiError } from "../api/client";
import {
  Badge, DataTable, EmptyState, ErrorNote, Field, FullPageSpinner, IdChip, Modal, Spinner, timeAgo,
} from "../components/ui";

/**
 * Curated, wildcard-ready patterns for the event picker. Deliberately row-events-only: the durable
 * outbox (`praxy.events`, what the Phase 6 dispatcher reads) is written exclusively by
 * `RowsService.WriteOutboxAsync` — user/team/session/membership actions publish to the in-process
 * realtime bus only, never the outbox, so a webhook subscribed to those patterns would never fire.
 * Offering them here would be a silently-dead option; the "add a custom pattern" field below still
 * accepts any grammar-valid string for narrowing to one database/table.
 */
const EVENT_PRESETS = [
  { pattern: "databases.*.tables.*.rows.*.create", label: "Row created" },
  { pattern: "databases.*.tables.*.rows.*.update", label: "Row updated" },
  { pattern: "databases.*.tables.*.rows.*.delete", label: "Row deleted" },
] as const;

const HEADERS = ["Name", "URL", "Events", "Status", "Created", ""];

export function WebhooksPage() {
  const { projectId } = useParams({ strict: false }) as { projectId: string };
  const webhooks = useWebhooks(projectId);
  const update = useUpdateWebhook(projectId);
  const remove = useDeleteWebhook(projectId);
  const [creating, setCreating] = useState(false);

  if (webhooks.isPending) return <FullPageSpinner />;
  if (webhooks.isError) throw webhooks.error;

  const disabled = webhooks.data.webhooks.filter((w) => !w.enabled && w.disabledReason);

  return (
    <div>
      <div className="mb-6 flex items-center justify-between">
        <h1 className="text-2xl font-semibold tracking-tight">Webhooks</h1>
        <button type="button" className="btn-primary" onClick={() => setCreating(true)}>
          + Create webhook
        </button>
      </div>

      {disabled.length > 0 ? (
        <div className="mb-4 rounded-lg border border-amber-400/30 bg-amber-400/10 px-3 py-2 text-sm text-amber-400">
          {disabled.length === 1
            ? `"${disabled[0].name}" was disabled automatically after repeated delivery failures.`
            : `${disabled.length} webhooks were disabled automatically after repeated delivery failures.`}{" "}
          Fix the endpoint, then re-enable below.
        </div>
      ) : null}

      {creating ? <CreateWebhookModal projectId={projectId} onClose={() => setCreating(false)} /> : null}

      {webhooks.data.total === 0 ? (
        <EmptyState
          headers={HEADERS}
          title="No webhooks. Register one to receive signed HTTP deliveries on row events."
          action={
            <button type="button" className="btn-primary" onClick={() => setCreating(true)}>
              + Create webhook
            </button>
          }
        />
      ) : (
        <DataTable headers={HEADERS}>
          {webhooks.data.webhooks.map((webhook) => (
            <tr key={webhook.id}>
              <td className="px-4 py-3">
                <Link
                  to="/project/$projectId/webhooks/$webhookId"
                  params={{ projectId, webhookId: webhook.id }}
                  className="font-medium text-ink-100 hover:text-iris-300"
                >
                  {webhook.name}
                </Link>
                <div className="mt-1"><IdChip id={webhook.id} /></div>
              </td>
              <td className="max-w-64 truncate px-4 py-3 font-mono text-xs text-ink-400" title={webhook.url}>
                {webhook.url}
              </td>
              <td className="px-4 py-3 text-xs text-ink-400">{webhook.events.length} pattern(s)</td>
              <td className="px-4 py-3">
                {webhook.enabled ? (
                  <Badge tone="mint">enabled</Badge>
                ) : (
                  <span title={webhook.disabledReason ?? undefined}>
                    <Badge tone="coral">disabled</Badge>
                  </span>
                )}
              </td>
              <td className="px-4 py-3 whitespace-nowrap text-ink-400">{timeAgo(webhook.createdAt)}</td>
              <td className="px-4 py-3 text-right whitespace-nowrap">
                <button
                  type="button"
                  className="btn-ghost border border-ink-700 px-2 py-1 text-xs"
                  disabled={update.isPending}
                  onClick={() => update.mutate({ webhookId: webhook.id, enabled: !webhook.enabled })}
                >
                  {webhook.enabled ? "Disable" : "Enable"}
                </button>{" "}
                <button
                  type="button"
                  className="btn-ghost border border-ink-700 px-2 py-1 text-xs text-coral-400"
                  disabled={remove.isPending}
                  onClick={() => remove.mutate(webhook.id)}
                >
                  Delete
                </button>
              </td>
            </tr>
          ))}
        </DataTable>
      )}
    </div>
  );
}

function CreateWebhookModal({ projectId, onClose }: { projectId: string; onClose: () => void }) {
  const create = useCreateWebhook(projectId);
  const [name, setName] = useState("");
  const [url, setUrl] = useState("");
  const [events, setEvents] = useState<string[]>([]);
  const [customPattern, setCustomPattern] = useState("");
  const [secret, setSecret] = useState<string | null>(null);
  const [copied, setCopied] = useState(false);
  const error = create.error instanceof ApiError ? create.error : null;

  async function onSubmit(e: FormEvent) {
    e.preventDefault();
    const created = await create.mutateAsync({ name, url, events });
    setSecret(created.secret);
  }

  function addCustomPattern() {
    const trimmed = customPattern.trim();
    if (trimmed && !events.includes(trimmed)) setEvents((current) => [...current, trimmed]);
    setCustomPattern("");
  }

  // Reveal-once: after creation the modal becomes the one and only display of the signing secret.
  if (secret !== null) {
    return (
      <Modal title="Copy your signing secret" onClose={onClose}>
        <p className="mb-3 text-sm text-ink-400">
          This is the only time the secret is shown. Verify every delivery's{" "}
          <code className="text-ink-300">X-Praxy-Webhook-Signature</code> header against it — Praxy
          does not keep a second copy you can re-reveal.
        </p>
        <div className="flex items-stretch gap-2">
          <pre className="flex-1 overflow-x-auto rounded-lg border border-ink-700 bg-ink-950 px-3 py-2.5 font-mono text-xs text-ink-100">
            {secret}
          </pre>
          <button
            type="button"
            className="btn-ghost shrink-0 border border-ink-700"
            onClick={() => {
              void navigator.clipboard.writeText(secret);
              setCopied(true);
              setTimeout(() => setCopied(false), 1200);
            }}
          >
            {copied ? "✓" : "Copy"}
          </button>
        </div>
        <button type="button" className="btn-primary mt-4 w-full" onClick={onClose}>
          Done
        </button>
      </Modal>
    );
  }

  return (
    <Modal title="Create webhook" onClose={onClose}>
      <form onSubmit={(e) => void onSubmit(e)} className="space-y-4">
        {error && !error.envelope.fields ? <ErrorNote message={error.message} /> : null}
        <Field label="Name" error={error?.fieldErrors("name")[0]}>
          <input
            className="input-base"
            required
            autoFocus
            value={name}
            onChange={(e) => setName(e.target.value)}
            placeholder="Order sync"
          />
        </Field>
        <Field label="URL" error={error?.fieldErrors("url")[0]}>
          <input
            className="input-base"
            required
            type="url"
            value={url}
            onChange={(e) => setUrl(e.target.value)}
            placeholder="https://example.com/webhooks/praxy"
          />
        </Field>
        <div>
          <span className="mb-1.5 block text-xs font-medium uppercase tracking-wide text-ink-400">Events</span>
          <div className="grid grid-cols-1 gap-2">
            {EVENT_PRESETS.map((preset) => (
              <label
                key={preset.pattern}
                className={`flex cursor-pointer items-center gap-2 rounded-lg border px-3 py-2 text-sm transition-colors ${
                  events.includes(preset.pattern)
                    ? "border-iris-500/60 bg-iris-500/10 text-ink-100"
                    : "border-ink-700 text-ink-400 hover:border-ink-500"
                }`}
              >
                <input
                  type="checkbox"
                  className="hidden"
                  checked={events.includes(preset.pattern)}
                  onChange={(e) =>
                    setEvents((current) =>
                      e.target.checked ? [...current, preset.pattern] : current.filter((p) => p !== preset.pattern),
                    )
                  }
                />
                <span>{preset.label}</span>
                <span className="ml-auto font-mono text-[11px] text-ink-500">{preset.pattern}</span>
              </label>
            ))}
          </div>

          {events.filter((e) => !EVENT_PRESETS.some((p) => p.pattern === e)).length > 0 ? (
            <div className="mt-2 flex flex-wrap gap-1">
              {events
                .filter((e) => !EVENT_PRESETS.some((p) => p.pattern === e))
                .map((pattern) => (
                  <span
                    key={pattern}
                    className="inline-flex items-center gap-1 rounded-md border border-ink-700 bg-ink-900 px-2 py-0.5 font-mono text-[11px] text-ink-300"
                  >
                    {pattern}
                    <button
                      type="button"
                      className="text-ink-500 hover:text-coral-400"
                      onClick={() => setEvents((current) => current.filter((p) => p !== pattern))}
                    >
                      ✕
                    </button>
                  </span>
                ))}
            </div>
          ) : null}

          <div className="mt-2 flex gap-2">
            <input
              className="input-base flex-1 font-mono text-xs"
              value={customPattern}
              onChange={(e) => setCustomPattern(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === "Enter") {
                  e.preventDefault();
                  addCustomPattern();
                }
              }}
              placeholder="Custom pattern, e.g. databases.<id>.tables.*.rows.*.update"
            />
            <button type="button" className="btn-ghost shrink-0 border border-ink-700 text-xs" onClick={addCustomPattern}>
              Add
            </button>
          </div>
          {error?.fieldErrors("events")[0] ? (
            <span className="mt-1 block text-xs text-coral-400">{error.fieldErrors("events")[0]}</span>
          ) : null}
        </div>
        <button type="submit" className="btn-primary w-full" disabled={create.isPending || events.length === 0}>
          {create.isPending ? <Spinner /> : "Create webhook"}
        </button>
      </form>
    </Modal>
  );
}
