import { Link, useNavigate, useParams } from "@tanstack/react-router";
import { useState } from "react";
import { ApiError } from "../api/client";
import {
  useConnectionCount,
  useDeleteProject,
  useProject,
  useProjectOverview,
  useQuotas,
  useUpdateProject,
} from "../api/queries";
import { ErrorNote, FullPageSpinner, IdChip, InlineEditableTitle, PageHeader, Spinner } from "../components/ui";
import { formatBytes } from "./storageFormat";

export function ProjectOverviewPage() {
  const { projectId } = useParams({ strict: false }) as { projectId: string };
  const project = useProject(projectId, { pollWhileUnpinged: true });
  const update = useUpdateProject(projectId);

  if (project.isPending) return <FullPageSpinner />;
  if (project.isError) throw project.error;

  // Narrowing a local, not Boolean(...) + a `!` at the use site: lastPingAt is omitted from the
  // JSON until the first ping, so the value here is undefined and the assertion was asserting away
  // the very case the guard exists for.
  const lastPingAt = project.data.lastPingAt;

  return (
    <div>
      <PageHeader
        title={<InlineEditableTitle value={project.data.name} onSave={(name) => update.mutateAsync({ name })} />}
        chips={
          <>
            <IdChip id={project.data.id} />
            {/* The console is served from the same origin as the API, so this is always this
                instance's real endpoint — and it is the one value every SDK setup asks for first. */}
            <IdChip id={`${window.location.origin}/v1`} title="Copy API endpoint" />
          </>
        }
        description={`Created ${new Date(project.data.createdAt).toLocaleString()}`}
      />

      <ResourceTiles projectId={project.data.id} />

      {/* Two columns from `lg` up: the connection state and its live counter on the left, usage on
          the right. Stacked full-width cards left most of a desktop viewport empty. */}
      <div className="mt-4 grid grid-cols-1 items-start gap-4 lg:grid-cols-2">
        <div className="space-y-4">
          {lastPingAt ? <ConnectedCard lastPingAt={lastPingAt} /> : <WaitingCard projectId={project.data.id} />}
          <ConnectionsTile projectId={project.data.id} />
          <ActivityCard projectId={project.data.id} />
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
 * What exists in this project, and where to go next — the question the overview screen was not
 * answering before, which left an operator opening a project with no idea whether it had two
 * databases or none without visiting every screen.
 *
 * Counts come from one endpoint rather than a dozen list calls read for their `total`; see
 * `ProjectOverviewResponse`. A tile with nothing in it still renders, because "0 users" is
 * information — the empty tiles are the ones worth clicking on a new project.
 */
function ResourceTiles({ projectId }: { projectId: string }) {
  const overview = useProjectOverview(projectId);
  if (!overview.data) return null;
  const o = overview.data;

  const tiles: Array<{ to: string; label: string; value: number; hint?: string }> = [
    { to: "/project/$projectId/databases", label: "Databases", value: o.databases, hint: `${o.tables} ${o.tables === 1 ? "table" : "tables"}` },
    { to: "/project/$projectId/auth/users", label: "Users", value: o.users, hint: `${o.teams} ${o.teams === 1 ? "team" : "teams"}` },
    { to: "/project/$projectId/functions", label: "Functions", value: o.functions },
    { to: "/project/$projectId/sites", label: "Sites", value: o.sites },
    { to: "/project/$projectId/storage", label: "Buckets", value: o.buckets },
    { to: "/project/$projectId/messaging", label: "Topics", value: o.messagingTopics },
    { to: "/project/$projectId/webhooks", label: "Webhooks", value: o.webhooks },
    { to: "/project/$projectId/api-keys", label: "API keys", value: o.apiKeys, hint: `${o.platforms} ${o.platforms === 1 ? "platform" : "platforms"}` },
  ];

  return (
    <div className="mb-4 grid grid-cols-2 gap-3 sm:grid-cols-4">
      {tiles.map((tile) => (
        <Link
          key={tile.label}
          to={tile.to}
          params={{ projectId }}
          className="surface flex flex-col gap-0.5 p-4 transition-colors hover:border-iris-500/60"
        >
          <span className="text-2xl font-semibold tabular-nums text-ink-100">{tile.value}</span>
          <span className="text-sm text-ink-400">{tile.label}</span>
          {tile.hint ? <span className="text-xs text-ink-600">{tile.hint}</span> : null}
        </Link>
      ))}
    </div>
  );
}

/**
 * The two things Praxy actually meters. There is deliberately no "requests" or "bandwidth" figure
 * for the data plane — nothing counts those, and a number that looked like API traffic while only
 * measuring part of it would be worse than its absence.
 *
 * Seven days because that is `Praxy:Retention:SiteRequestsMaxAgeDays`' default: a longer window
 * would quietly under-report as rows age out.
 */
function ActivityCard({ projectId }: { projectId: string }) {
  const overview = useProjectOverview(projectId);
  if (!overview.data) return null;

  return (
    <div className="surface p-6">
      <h2 className="mb-1 text-lg font-medium">Activity</h2>
      <p className="mb-4 text-xs text-ink-500">Last 7 days.</p>
      <div className="grid grid-cols-2 gap-4">
        <Link
          to="/project/$projectId/sites"
          params={{ projectId }}
          className="rounded-lg border border-ink-800 p-3 transition-colors hover:border-ink-600"
        >
          <div className="text-xl font-semibold tabular-nums">{overview.data.siteRequestsLast7Days}</div>
          <div className="text-xs text-ink-500">Site requests</div>
        </Link>
        <Link
          to="/project/$projectId/functions"
          params={{ projectId }}
          className="rounded-lg border border-ink-800 p-3 transition-colors hover:border-ink-600"
        >
          <div className="text-xl font-semibold tabular-nums">{overview.data.functionExecutionsLast7Days}</div>
          <div className="text-xs text-ink-500">Function executions</div>
        </Link>
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
