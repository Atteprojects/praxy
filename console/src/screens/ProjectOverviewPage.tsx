import { Link, useParams } from "@tanstack/react-router";
import { useState } from "react";
import { useConnectionCount, useProject, useProjectOverview, useQuotas } from "../api/queries";
import { FullPageSpinner, IdChip, PageHeader } from "../components/ui";
import { formatBytes } from "./storageFormat";

export function ProjectOverviewPage() {
  const { projectId } = useParams({ strict: false }) as { projectId: string };
  const project = useProject(projectId, { pollWhileUnpinged: true });

  if (project.isPending) return <FullPageSpinner />;
  if (project.isError) throw project.error;

  // Narrowing a local, not Boolean(...) + a `!` at the use site: lastPingAt is omitted from the
  // JSON until the first ping, so the value here is undefined and the assertion was asserting away
  // the very case the guard exists for.
  const lastPingAt = project.data.lastPingAt;

  return (
    <div>
      <PageHeader
        title={<h1 className="text-2xl font-semibold tracking-tight">{project.data.name}</h1>}
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

      {/* One vertical rhythm, full width, rather than two ragged columns: status, what exists,
          what it has been doing, then the destructive action last. */}
      <div className="space-y-8">
        {lastPingAt ? <ConnectedBar lastPingAt={lastPingAt} /> : <WaitingCard projectId={project.data.id} />}

        <section>
          <h2 className="mb-3 text-sm font-medium tracking-widest text-ink-500 uppercase">Activity</h2>
          <ActivityTiles projectId={project.data.id} />
        </section>

        <section>
          <h2 className="mb-3 text-sm font-medium tracking-widest text-ink-500 uppercase">Resources</h2>
          <ResourceTiles projectId={project.data.id} />
        </section>

        <QuotaWarnings projectId={project.data.id} />
      </div>
    </div>
  );
}

/**
 * What exists in this project, and where to go next — the question the overview was not answering
 * before, which left an operator opening a project unable to tell whether it had two databases or
 * none without visiting every screen.
 *
 * Limits live on the tiles rather than in a card of their own. A separate usage panel repeated
 * three of these counts and, on a project nowhere near any ceiling, was eight progress bars all
 * reading near-empty — noise in the common case, in exchange for information that only matters as
 * you approach a wall. `QuotaWarnings` below covers approaching the wall; this covers the rest of
 * the time.
 */
function ResourceTiles({ projectId }: { projectId: string }) {
  const overview = useProjectOverview(projectId);
  const quotas = useQuotas(projectId);
  if (!overview.data) return null;
  const o = overview.data;
  const q = quotas.data;

  // `max` only where a quota actually exists — users, teams, functions, webhooks, topics, keys and
  // platforms have none, and inventing a ceiling for them would be worse than showing none.
  const tiles: Array<{ to: string; label: string; value: number; max?: number; hint?: string }> = [
    { to: "/project/$projectId/databases", label: "Databases", value: o.databases, max: q?.databasesMax, hint: `${o.tables} ${o.tables === 1 ? "table" : "tables"}` },
    { to: "/project/$projectId/auth/users", label: "Users", value: o.users, hint: `${o.teams} ${o.teams === 1 ? "team" : "teams"}` },
    { to: "/project/$projectId/functions", label: "Functions", value: o.functions },
    { to: "/project/$projectId/sites", label: "Sites", value: o.sites, max: q?.sitesMax },
    {
      to: "/project/$projectId/storage",
      label: "Buckets",
      value: o.buckets,
      max: q?.bucketsMax,
      // The one number worth keeping from the old usage panel: stored bytes bound how large every
      // backup gets, since files live in the schema backup.sh dumps (docs/self-host.md).
      hint: q ? `${formatBytes(q.storageBytesUsed)} of ${formatBytes(q.storageBytesMax)}` : undefined,
    },
    { to: "/project/$projectId/messaging", label: "Topics", value: o.messagingTopics },
    { to: "/project/$projectId/webhooks", label: "Webhooks", value: o.webhooks },
    { to: "/project/$projectId/api-keys", label: "API keys", value: o.apiKeys, hint: `${o.platforms} ${o.platforms === 1 ? "platform" : "platforms"}` },
  ];

  return (
    <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
      {tiles.map((tile) => {
        const pressure = tile.max && tile.max > 0 ? tile.value / tile.max : 0;
        const valueColor = pressure >= 1 ? "text-red-400" : pressure >= 0.8 ? "text-amber-400" : "text-ink-100";
        return (
          <Link
            key={tile.label}
            to={tile.to}
            params={{ projectId }}
            className="surface flex flex-col gap-0.5 p-4 transition-colors hover:border-iris-500/60"
          >
            <span className="flex items-baseline gap-1.5">
              <span className={`text-2xl font-semibold tabular-nums ${valueColor}`}>{tile.value}</span>
              {tile.max ? <span className="text-xs text-ink-600 tabular-nums">of {tile.max}</span> : null}
            </span>
            <span className="text-sm text-ink-400">{tile.label}</span>
            {tile.hint ? <span className="truncate text-xs text-ink-600">{tile.hint}</span> : null}
          </Link>
        );
      })}
    </div>
  );
}

/**
 * The three things Praxy actually meters, in the same tile shape as Resources so the page has one
 * visual vocabulary rather than a stack of differently-sized cards.
 *
 * There is deliberately no "requests" or "bandwidth" total for the data plane — nothing counts
 * those, and a figure that read as API traffic while measuring only the slice we happen to log
 * would be worse than its absence. Seven days because that is
 * `Praxy:Retention:SiteRequestsMaxAgeDays`' default: a longer window would quietly under-report as
 * rows age out. Live connections is a right-now number, labelled as such.
 */
function ActivityTiles({ projectId }: { projectId: string }) {
  const overview = useProjectOverview(projectId);
  const connections = useConnectionCount(projectId);
  if (!overview.data) return null;

  // Three across the full row, not three of four — these lead the page, so a trailing empty cell
  // read as a missing fourth metric rather than as deliberate space.
  return (
    <div className="grid grid-cols-1 gap-3 sm:grid-cols-3">
      <ActivityTile
        projectId={projectId}
        to="/project/$projectId/sites"
        value={overview.data.siteRequestsLast7Days}
        label="Site requests"
        hint="last 7 days"
      />
      <ActivityTile
        projectId={projectId}
        to="/project/$projectId/functions"
        value={overview.data.functionExecutionsLast7Days}
        label="Function executions"
        hint="last 7 days"
      />
      <ActivityTile
        projectId={projectId}
        to="/project/$projectId/realtime"
        value={connections.data?.count ?? "—"}
        label="Live connections"
        hint="right now"
      />
    </div>
  );
}

function ActivityTile({
  projectId,
  to,
  value,
  label,
  hint,
}: {
  projectId: string;
  to: string;
  value: number | string;
  label: string;
  hint: string;
}) {
  return (
    <Link
      to={to}
      params={{ projectId }}
      className="surface flex flex-col gap-0.5 p-5 transition-colors hover:border-iris-500/60"
    >
      <span className="text-3xl font-semibold tabular-nums text-ink-100">{value}</span>
      <span className="truncate text-sm text-ink-300">{label}</span>
      <span className="text-xs text-ink-600">{hint}</span>
    </Link>
  );
}

/**
 * Quota pressure, and *only* pressure — this renders nothing at all until something is at 80% of
 * its limit.
 *
 * It replaces an always-visible usage panel that showed all eight dimensions as progress bars.
 * That panel duplicated three of the resource tiles above and, on any project not near a ceiling,
 * was a column of near-empty bars: permanent cost for information that only matters occasionally.
 * The per-database and per-table dimensions have nowhere else to appear, though, and hitting the
 * columns-per-table limit mid-DDL with no warning is a genuinely bad surprise — so they are kept
 * here, silent until they are worth reading.
 */
function QuotaWarnings({ projectId }: { projectId: string }) {
  const quotas = useQuotas(projectId);
  if (!quotas.data) return null;
  const q = quotas.data;

  const dimensions: Array<{ label: string; used: number; max: number; format?: (v: number) => string }> = [
    { label: "Projects in this organization", used: q.projectsUsed, max: q.projectsMax },
    { label: "Databases", used: q.databasesUsed, max: q.databasesMax },
    { label: "Tables in the busiest database", used: q.busiestDatabaseTables, max: q.tablesPerDatabaseMax },
    { label: "Columns in the busiest table", used: q.busiestTableColumns, max: q.columnsPerTableMax },
    { label: "Indexes in the busiest table", used: q.busiestTableIndexes, max: q.indexesPerTableMax },
    { label: "Sites", used: q.sitesUsed, max: q.sitesMax },
    { label: "Buckets", used: q.bucketsUsed, max: q.bucketsMax },
    { label: "Stored files", used: q.storageBytesUsed, max: q.storageBytesMax, format: formatBytes },
  ];

  const pressured = dimensions.filter((d) => d.max > 0 && d.used / d.max >= 0.8);
  if (pressured.length === 0) return null;

  return (
    <section className="surface border-amber-400/20 p-5">
      <h2 className="mb-3 text-sm font-medium text-amber-400">Approaching a limit</h2>
      <div className="space-y-3">
        {pressured.map((d) => {
          const ratio = d.used / d.max;
          const format = d.format ?? ((v: number) => String(v));
          return (
            <div key={d.label}>
              <div className="mb-1 flex items-center justify-between text-sm">
                <span className="text-ink-400">{d.label}</span>
                <span className={`tabular-nums ${ratio >= 1 ? "text-red-400" : "text-amber-400"}`}>
                  {format(d.used)} / {format(d.max)}
                </span>
              </div>
              <div className="h-1.5 overflow-hidden rounded-full bg-ink-800">
                <div
                  className={`h-full rounded-full ${ratio >= 1 ? "bg-red-500" : "bg-amber-400"}`}
                  style={{ width: `${Math.min(100, ratio * 100)}%` }}
                />
              </div>
            </div>
          );
        })}
      </div>
    </section>
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

function ConnectedBar({ lastPingAt }: { lastPingAt: string }) {
  return (
    <div className="flex items-center gap-2.5 text-sm text-ink-400">
      <span className="size-2 shrink-0 rounded-full bg-mint-400" />
      <span className="text-ink-300">Connected</span>
      <span className="text-ink-600">·</span>
      <span>last ping {new Date(lastPingAt).toLocaleString()}</span>
    </div>
  );
}
