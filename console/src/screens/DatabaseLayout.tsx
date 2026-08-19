import { Outlet, useParams } from "@tanstack/react-router";
import { useState, type ReactNode } from "react";
import { useDatabase, useTables } from "../api/databases";
import { TablesIcon } from "../components/icons";
import { FullPageSpinner, Sheet } from "../components/ui";
import { STR } from "../strings";
import { TablesPanelContent } from "./TablesPanel";

/**
 * Database-scoped routes. The tables panel itself is rendered by `ProjectLayout`, beside the icon
 * rail, so it can sit flush and outside the content padding; what stays here is the below-`md`
 * drawer that stands in for it and the routed content.
 */
export function DatabaseLayout() {
  const { projectId, databaseId } = useParams({ strict: false }) as { projectId: string; databaseId: string };
  const database = useDatabase(projectId, databaseId);
  const [tablesOpen, setTablesOpen] = useState(false);

  if (database.isPending) return <FullPageSpinner />;
  if (database.isError) throw database.error;

  return (
    <div className="min-w-0">
      <div className="mb-6 flex items-center justify-between gap-3 md:hidden">
        <span className="truncate text-sm font-medium text-ink-200">{database.data.name}</span>
        <button
          type="button"
          className="btn-ghost shrink-0 border border-ink-700 text-xs"
          onClick={() => setTablesOpen(true)}
        >
          <TablesIcon className="size-4" />
          {STR.tables}
        </button>
      </div>

      <Outlet />

      {tablesOpen ? (
        <Sheet side="left" title={database.data.name} onClose={() => setTablesOpen(false)}>
          <div onClick={(e) => (e.target as HTMLElement).closest("a") && setTablesOpen(false)}>
            <TablesPanelContent projectId={projectId} databaseId={databaseId} compact />
          </div>
        </Sheet>
      ) : null}
    </div>
  );
}

/** Rendered at the bare `databases/$databaseId` route — no table selected yet. */
export function DatabaseIndexPage() {
  const { projectId, databaseId } = useParams({ strict: false }) as { projectId: string; databaseId: string };
  const tables = useTables(projectId, databaseId);

  if (tables.isPending) return <FullPageSpinner />;
  if (tables.isError) throw tables.error;

  return (
    <GhostPanel>
      {tables.data.total === 0
        ? "Create your first table using the + Create button in the sidebar."
        : "Select a table from the sidebar."}
    </GhostPanel>
  );
}

function GhostPanel({ children }: { children: ReactNode }) {
  return (
    <div className="surface grid min-h-64 place-items-center">
      <p className="text-sm text-ink-500">{children}</p>
    </div>
  );
}
