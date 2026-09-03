import { Link, useNavigate, useParams } from "@tanstack/react-router";
import { useState } from "react";
import { ApiError } from "../api/client";
import { useConnectionCount, useDeleteProject, useProject, useQuotas, useUpdateProject } from "../api/queries";
import { ErrorNote, FullPageSpinner, IdChip, InlineEditableTitle, PageHeader, Spinner } from "../components/ui";
import { formatBytes } from "./storageFormat";

export function ProjectOverviewPage() {
  const { projectId } = useParams({ strict: false }) as { projectId: string };
  const project = useProject(projectId, { pollWhileUnpinged: true });
  const update = useUpdateProject(projectId);

  if (project.isPending) return <FullPageSpinner />;
  if (project.isError) throw project.error;

  const pinged = Boolean(project.data.lastPingAt);

  return (
    <div>
      <PageHeader
        title={<InlineEditableTitle value={project.data.name} onSave={(name) => update.mutateAsync({ name })} />}
        chips={<IdChip id={project.data.id} />}
        description={`Created ${new Date(project.data.createdAt).toLocaleString()}`}
      />

      {/* Two columns from `lg` up: the connection state and its live counter on the left, usage on
          the right. Stacked full-width cards left most of a desktop viewport empty. */}
      <div className="grid grid-cols-1 items-start gap-4 lg:grid-cols-2">
        <div className="space-y-4">
          {pinged ? <ConnectedCard lastPingAt={project.data.lastPingAt!} /> : <WaitingCard projectId={project.data.id} />}
          <ConnectionsTile projectId={project.data.id} />
        </div>
        <QuotaCard projectId={project.data.id} />
      </div>

      <DangerZone projectId={project.data.id} projectName={project.data.name} />
    </div>
  );
}

/**
 * A hard, confirmed delete — the gap analysis explicitly asked for this, not archiving or a
 * soft-delete. Typed-name confirmation, the same shape `TableSettingsPage.tsx` and
 * `DatabasesPage.tsx`'s database-delete already use: this is strictly more destructive than either
 * (every database, user, key, team and function in the project goes with it), so it gets at least
 * as much friction, not a one-click `ConfirmButton`.
 */
function DangerZone({ projectId, projectName }: { projectId: string; projectName: string }) {
  const navigate = useNavigate();
  const remove = useDeleteProject(projectId);
  const [confirmName, setConfirmName] = useState("");
  const error = remove.error instanceof ApiError ? remove.error : null;

  async function onDelete() {
    await remove.mutateAsync();
    await navigate({ to: "/" });
  }

  return (
    <div className="mt-8 max-w-3xl surface border-coral-400/20 p-5">
      <h2 className="mb-3 text-sm font-medium text-coral-400">Danger zone</h2>
      <p className="mb-3 text-xs text-ink-500">
        Deleting <span className="font-mono text-ink-300">{projectName}</span> removes every
        database (and every table, column, index and row inside them), every function, user,
        API key and team in this project. This cannot be undone.
      </p>
      {error ? <div className="mb-3"><ErrorNote message={error.message} /></div> : null}
      <p className="mb-2 text-xs text-ink-500">
        Type <span className="font-mono text-ink-300">{projectName}</span> to confirm.
      </p>
      <div className="flex gap-2">
        <input
          className="input-base flex-1"
          value={confirmName}
          onChange={(e) => setConfirmName(e.target.value)}
          placeholder={projectName}
        />
        <button
          type="button"
          className="btn-ghost shrink-0 border border-coral-400/60 text-coral-400 disabled:opacity-40"
          disabled={confirmName !== projectName || remove.isPending}
          onClick={() => void onDelete()}
        >
          {remove.isPending ? <Spinner /> : "Delete project"}
        </button>
      </div>
    </div>
  );
}

/**
 * Org-level quota usage (roadmap Phase 9). The owning organization is named on the console home,
 * but there is still no org switcher and no cross-project view: this shows this project's own
 * numbers against the effective limit (org override, else instance default).
 */
function QuotaCard({ projectId }: { projectId: string }) {
  const quotas = useQuotas(projectId);
  if (!quotas.data) return null;

  const rows: Array<{ label: string; used: number; max: number; format?: (value: number) => string }> = [
    { label: "Projects (organization)", used: quotas.data.projectsUsed, max: quotas.data.projectsMax },
    { label: "Databases", used: quotas.data.databasesUsed, max: quotas.data.databasesMax },
    { label: "Tables (busiest database)", used: quotas.data.busiestDatabaseTables, max: quotas.data.tablesPerDatabaseMax },
    { label: "Columns (busiest table)", used: quotas.data.busiestTableColumns, max: quotas.data.columnsPerTableMax },
    { label: "Indexes (busiest table)", used: quotas.data.busiestTableIndexes, max: quotas.data.indexesPerTableMax },
    { label: "Sites", used: quotas.data.sitesUsed, max: quotas.data.sitesMax },
    { label: "Buckets", used: quotas.data.bucketsUsed, max: quotas.data.bucketsMax },
    // Bytes rather than a count: this is the dimension that bounds how large every backup gets,
    // since stored files live in the schema deploy/backup.sh dumps (docs/self-host.md).
    {
      label: "Stored files",
      used: quotas.data.storageBytesUsed,
      max: quotas.data.storageBytesMax,
      format: formatBytes,
    },
  ];

  return (
    <div className="surface p-6">
      <h2 className="mb-4 text-lg font-medium">Usage</h2>
      <div className="space-y-3">
        {rows.map((row) => (
          <QuotaRow key={row.label} {...row} />
        ))}
      </div>
    </div>
  );
}

function QuotaRow({
  label,
  used,
  max,
  format = (value: number) => String(value),
}: {
  label: string;
  used: number;
  max: number;
  format?: (value: number) => string;
}) {
  const ratio = max > 0 ? used / max : 0;
  const barColor = ratio >= 1 ? "bg-red-500" : ratio >= 0.8 ? "bg-amber-400" : "bg-mint-400";
  const textColor = ratio >= 1 ? "text-red-400" : ratio >= 0.8 ? "text-amber-400" : "text-ink-300";

  return (
    <div>
      <div className="mb-1 flex items-center justify-between text-sm">
        <span className="text-ink-400">{label}</span>
        <span className={`tabular-nums ${textColor}`}>
          {format(used)} / {format(max)}
        </span>
      </div>
      <div className="h-1.5 overflow-hidden rounded-full bg-ink-800">
        <div
          className={`h-full rounded-full ${barColor}`}
          style={{ width: `${Math.min(100, ratio * 100)}%` }}
        />
      </div>
    </div>
  );
}

/** The realtime inspector's cheapest possible advertisement: a live count, updating on its own. */
function ConnectionsTile({ projectId }: { projectId: string }) {
  const connections = useConnectionCount(projectId);
  return (
    <Link
      to="/project/$projectId/realtime"
      params={{ projectId }}
      className="surface flex items-center justify-between p-6 transition-colors hover:border-ink-600"
    >
      <div>
        <h2 className="text-lg font-medium">Realtime</h2>
        <p className="mt-0.5 text-sm text-ink-400">Live WebSocket connections on this project.</p>
      </div>
      <span className="text-3xl font-semibold tabular-nums text-ink-100">
        {connections.data?.count ?? "—"}
      </span>
    </Link>
  );
}

/** Onboarding: shown until the first real API ping lands, then flips automatically. */
function WaitingCard({ projectId }: { projectId: string }) {
  const snippet = `curl ${window.location.origin}/v1/ping -H "X-Praxy-Project: ${projectId}"`;
  const [copied, setCopied] = useState(false);

  return (
    <div className="surface p-6">
      <div className="mb-4 flex items-center gap-3">
        <span className="size-2.5 rounded-full bg-amber-400 animate-ping-pulse" />
        <h2 className="text-lg font-medium">Waiting for your first ping…</h2>
      </div>
      <p className="mb-4 text-sm text-ink-400">
        Send any request with your project header and this screen updates the moment it arrives.
      </p>
      <div className="flex items-stretch gap-2">
        <pre className="flex-1 overflow-x-auto rounded-lg border border-ink-700 bg-ink-950 px-4 py-3 font-mono text-xs text-ink-300">
          {snippet}
        </pre>
        <button
          type="button"
          className="btn-ghost border border-ink-700"
          onClick={() => {
            void navigator.clipboard.writeText(snippet);
            setCopied(true);
            setTimeout(() => setCopied(false), 1200);
          }}
        >
          {copied ? "✓" : "Copy"}
        </button>
      </div>
    </div>
  );
}

function ConnectedCard({ lastPingAt }: { lastPingAt: string }) {
  return (
    <div className="surface p-6">
      <div className="mb-2 flex items-center gap-3">
        <span className="size-2.5 rounded-full bg-mint-400" />
        <h2 className="text-lg font-medium">Connected</h2>
      </div>
      <p className="text-sm text-ink-400">
        Last ping {new Date(lastPingAt).toLocaleString()}. Head to Users and Teams to manage who can
        sign in, Databases to model your data, or Functions, Webhooks and Messaging to react to it.
      </p>
    </div>
  );
}
