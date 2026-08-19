import { useParams } from "@tanstack/react-router";
import { useEffect, useMemo, useState } from "react";
import { useAccount } from "../api/queries";
import { useProjectAuditLog, type AuditLogFilters } from "../api/audit";
import type { AuditLogEntry } from "../api/types";
import type { DataGridColumn } from "../components/DataGrid";
import { DataGrid } from "../components/DataGrid";
import { Badge, EmptyState, FullPageSpinner, PageHeader, timeAgo } from "../components/ui";

const HEADERS = ["Time", "Actor", "Action", "Resource", "IP"];
const PAGE_SIZE = 50;
const FILTER_KEYS = ["action", "actor", "resource", "from", "to"] as const;

function readFiltersFromUrl(): AuditLogFilters {
  const params = new URLSearchParams(window.location.search);
  const filters: AuditLogFilters = {};
  for (const key of FILTER_KEYS) {
    const value = params.get(key);
    if (value) filters[key] = value;
  }
  return filters;
}

function writeFiltersToUrl(filters: AuditLogFilters) {
  const url = new URL(window.location.href);
  for (const key of FILTER_KEYS) {
    const value = filters[key];
    if (value) url.searchParams.set(key, value);
    else url.searchParams.delete(key);
  }
  window.history.replaceState(null, "", url.toString());
}

/**
 * Who did what, to which resource, when — not what changed. There is no diff/detail column
 * (praxy.audit_log records action + resource only), and this screen must not imply otherwise.
 *
 * Instance-level entries (a NULL project_id — today just `instance.claim`) are deliberately not
 * shown here: they belong to no project, and the instance is single-operator by construction, so
 * a second screen for one entry that will only ever exist once isn't worth building. They are
 * still readable via `GET /v1/console/audit` if that ever changes.
 */
export function AuditLogPage() {
  const { projectId } = useParams({ strict: false }) as { projectId: string };
  const account = useAccount();
  const [filters, setFilters] = useState<AuditLogFilters>(() => readFiltersFromUrl());
  const [offset, setOffset] = useState(0);

  useEffect(() => writeFiltersToUrl(filters), [filters]);
  useEffect(() => setOffset(0), [filters.action, filters.actor, filters.resource, filters.from, filters.to]);

  const entries = useProjectAuditLog(projectId, { ...filters, offset, limit: PAGE_SIZE });
  const hasFilters = FILTER_KEYS.some((key) => Boolean(filters[key]));

  const columns = useMemo<DataGridColumn<AuditLogEntry>[]>(() => [
    {
      id: "time",
      header: "Time",
      cell: ({ row }) => (
        <span className="text-xs text-ink-400" title={row.original.createdAt}>
          {timeAgo(row.original.createdAt)}
        </span>
      ),
    },
    {
      id: "actor",
      header: "Actor",
      cell: ({ row }) => <ActorCell actor={row.original.actor} accountId={account.data?.id ?? null} />,
    },
    {
      id: "action",
      header: "Action",
      cell: ({ row }) => <span className="font-mono text-xs text-ink-100">{row.original.action}</span>,
    },
    {
      id: "resource",
      header: "Resource",
      cell: ({ row }) => <span className="font-mono text-xs text-ink-400">{row.original.resource}</span>,
    },
    {
      id: "ip",
      header: "IP",
      cell: ({ row }) => <span className="font-mono text-xs text-ink-500">{row.original.ip ?? "—"}</span>,
    },
  ], [account.data?.id]);

  if (entries.isPending) return <FullPageSpinner />;
  if (entries.isError) throw entries.error;

  const total = entries.data.total;
  const hasNext = offset + entries.data.entries.length < total;

  return (
    <div>
      <PageHeader
        title="Audit log"
        description="Console-authenticated actions, plus API-key writes to this project's users. Does not cover data-plane row writes — rows.create/update/delete here come only from the console's own row editor, never from an app user or key writing through the data plane."
      />

      <FilterBar filters={filters} onChange={setFilters} />

      {total === 0 ? (
        <EmptyState
          headers={HEADERS}
          title={
            hasFilters
              ? "No entries match your filters."
              : "No audit entries yet. They appear here as operators act on this project."
          }
        />
      ) : (
        <>
          <DataGrid columns={columns} data={entries.data.entries} getRowId={(row) => row.id} maxHeight="65vh" />
          <div className="mt-3 flex items-center justify-between text-xs text-ink-500">
            <span>{total} total</span>
            <div className="flex gap-1">
              <button
                type="button"
                className="btn-ghost border border-ink-700 px-2 py-1 text-xs disabled:opacity-30"
                disabled={offset === 0}
                onClick={() => setOffset((o) => Math.max(0, o - PAGE_SIZE))}
              >
                ← Prev
              </button>
              <button
                type="button"
                className="btn-ghost border border-ink-700 px-2 py-1 text-xs disabled:opacity-30"
                disabled={!hasNext}
                onClick={() => setOffset((o) => o + PAGE_SIZE)}
              >
                Next →
              </button>
            </div>
          </div>
        </>
      )}
    </div>
  );
}

function ActorCell({ actor, accountId }: { actor: string; accountId: string | null }) {
  const isYou = accountId !== null && actor === `admin:${accountId}`;
  return (
    <span className="inline-flex items-center gap-1.5">
      {isYou ? <Badge tone="iris">you</Badge> : null}
      <span className="font-mono text-xs text-ink-300">{actor}</span>
    </span>
  );
}

function FilterBar({
  filters,
  onChange,
}: {
  filters: AuditLogFilters;
  onChange: (filters: AuditLogFilters) => void;
}) {
  const hasFilters = FILTER_KEYS.some((key) => Boolean(filters[key]));
  return (
    <div className="mb-4 flex flex-wrap items-end gap-3">
      <label className="block">
        <span className="mb-1 block text-xs font-medium uppercase tracking-wide text-ink-500">Action</span>
        <input
          className="input-base w-44"
          placeholder="e.g. users.password.reset"
          value={filters.action ?? ""}
          onChange={(e) => onChange({ ...filters, action: e.target.value || undefined })}
        />
      </label>
      <label className="block">
        <span className="mb-1 block text-xs font-medium uppercase tracking-wide text-ink-500">Actor</span>
        <input
          className="input-base w-48"
          placeholder="admin:… or key:…"
          value={filters.actor ?? ""}
          onChange={(e) => onChange({ ...filters, actor: e.target.value || undefined })}
        />
      </label>
      <label className="block">
        <span className="mb-1 block text-xs font-medium uppercase tracking-wide text-ink-500">Resource</span>
        <input
          className="input-base w-56"
          placeholder="e.g. user/…"
          value={filters.resource ?? ""}
          onChange={(e) => onChange({ ...filters, resource: e.target.value || undefined })}
        />
      </label>
      <label className="block">
        <span className="mb-1 block text-xs font-medium uppercase tracking-wide text-ink-500">From</span>
        <input
          type="date"
          className="input-base"
          value={filters.from ? filters.from.slice(0, 10) : ""}
          onChange={(e) =>
            onChange({ ...filters, from: e.target.value ? `${e.target.value}T00:00:00.000Z` : undefined })}
        />
      </label>
      <label className="block">
        <span className="mb-1 block text-xs font-medium uppercase tracking-wide text-ink-500">To</span>
        <input
          type="date"
          className="input-base"
          value={filters.to ? filters.to.slice(0, 10) : ""}
          onChange={(e) =>
            onChange({ ...filters, to: e.target.value ? `${e.target.value}T23:59:59.999Z` : undefined })}
        />
      </label>
      {hasFilters ? (
        <button type="button" className="btn-ghost border border-ink-700 px-2 py-1 text-xs" onClick={() => onChange({})}>
          Clear filters
        </button>
      ) : null}
    </div>
  );
}
