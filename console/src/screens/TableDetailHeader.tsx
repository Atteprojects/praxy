import { Link } from "@tanstack/react-router";
import type { TableSchema } from "../api/types";
import { Badge, IdChip } from "../components/ui";
import { STR } from "../strings";

/**
 * Shared by Columns/Indexes/Settings: entity screen header (name + copyable id + status chips)
 * plus the tab row switching between the three table sub-views (console-design.md).
 */
export function TableDetailHeader({
  projectId,
  databaseId,
  table,
  active,
}: {
  projectId: string;
  databaseId: string;
  table: TableSchema;
  active: "rows" | "columns" | "indexes" | "settings";
}) {
  return (
    <div className="mb-6">
      <div className="mb-4 flex flex-wrap items-center gap-3">
        <h1 className="text-2xl font-semibold tracking-tight">{table.name}</h1>
        <IdChip id={table.id} />
        {!table.enabled ? <Badge tone="ink">disabled</Badge> : null}
        {table.rowSecurity ? <Badge tone="iris">{STR.rowSecurity}</Badge> : null}
      </div>
      <div className="flex gap-1 border-b border-ink-800" role="tablist">
        <TabLink to="rows" label={STR.rows} active={active === "rows"} projectId={projectId} databaseId={databaseId} tableId={table.id} />
        <TabLink to="columns" label={STR.columns} active={active === "columns"} projectId={projectId} databaseId={databaseId} tableId={table.id} />
        <TabLink to="indexes" label={STR.indexes} active={active === "indexes"} projectId={projectId} databaseId={databaseId} tableId={table.id} />
        <TabLink to="settings" label="Settings" active={active === "settings"} projectId={projectId} databaseId={databaseId} tableId={table.id} />
      </div>
    </div>
  );
}

const TAB_ROUTES = {
  rows: "/project/$projectId/databases/$databaseId/tables/$tableId/rows",
  columns: "/project/$projectId/databases/$databaseId/tables/$tableId/columns",
  indexes: "/project/$projectId/databases/$databaseId/tables/$tableId/indexes",
  settings: "/project/$projectId/databases/$databaseId/tables/$tableId/settings",
} as const;

function TabLink({
  to,
  label,
  active,
  projectId,
  databaseId,
  tableId,
}: {
  to: keyof typeof TAB_ROUTES;
  label: string;
  active: boolean;
  projectId: string;
  databaseId: string;
  tableId: string;
}) {
  const params = { projectId, databaseId, tableId };
  const className = `-mb-px border-b-2 px-3 py-2 text-sm font-medium transition-colors ${
    active ? "border-iris-400 text-ink-100" : "border-transparent text-ink-500 hover:text-ink-300"
  }`;
  return (
    <Link to={TAB_ROUTES[to]} params={params} className={className} role="tab" aria-selected={active}>
      {label}
    </Link>
  );
}
