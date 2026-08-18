import { Link, Outlet, useNavigate, useParams } from "@tanstack/react-router";
import { useState, type FormEvent, type ReactNode } from "react";
import { useCreateTable, useDatabase, useTables } from "../api/databases";
import { ApiError } from "../api/client";
import { TablesIcon } from "../components/icons";
import { ErrorNote, Field, FullPageSpinner, IdChip, Modal, Sheet, Spinner } from "../components/ui";
import { STR } from "../strings";

type Table = { id: string; name: string; enabled: boolean };

/**
 * The exception to the tabs pattern (console-design.md): a second sidebar listing tables, because
 * switching tables is the highest-frequency navigation in the whole console. Below `md` this
 * becomes its own drawer (a second one, nested under the project nav's own drawer) rather than a
 * second fixed sidebar — two fixed 208px+224px sidebars alone would exceed a phone's viewport.
 */
export function DatabaseLayout() {
  const { projectId, databaseId } = useParams({ strict: false }) as { projectId: string; databaseId: string };
  const database = useDatabase(projectId, databaseId);
  const tables = useTables(projectId, databaseId);
  const [creating, setCreating] = useState(false);
  const [tablesOpen, setTablesOpen] = useState(false);

  if (database.isPending || tables.isPending) return <FullPageSpinner />;
  if (database.isError) throw database.error;
  if (tables.isError) throw tables.error;

  const sorted = [...tables.data.tables].sort((a, b) => a.name.localeCompare(b.name));

  return (
    <div className="flex min-h-dvh gap-8">
      <aside className="sticky top-14 hidden max-h-[calc(100dvh-3.5rem)] w-56 shrink-0 flex-col md:flex">
        <DatabaseNavContent
          projectId={projectId}
          databaseId={databaseId}
          databaseName={database.data.name}
          databaseIdValue={database.data.id}
          tables={sorted}
          onCreate={() => setCreating(true)}
        />
      </aside>

      <div className="min-w-0 flex-1">
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
      </div>

      {tablesOpen ? (
        <Sheet side="left" title={STR.tables} onClose={() => setTablesOpen(false)}>
          <div onClick={(e) => (e.target as HTMLElement).closest("a") && setTablesOpen(false)}>
            <DatabaseNavContent
              projectId={projectId}
              databaseId={databaseId}
              databaseName={database.data.name}
              databaseIdValue={database.data.id}
              tables={sorted}
              onCreate={() => {
                setTablesOpen(false);
                setCreating(true);
              }}
              compact
            />
          </div>
        </Sheet>
      ) : null}

      {creating ? (
        <CreateTableModal projectId={projectId} databaseId={databaseId} onClose={() => setCreating(false)} />
      ) : null}
    </div>
  );
}

function DatabaseNavContent({
  projectId,
  databaseId,
  databaseName,
  databaseIdValue,
  tables,
  onCreate,
  compact,
}: {
  projectId: string;
  databaseId: string;
  databaseName: string;
  databaseIdValue: string;
  tables: Table[];
  onCreate: () => void;
  compact?: boolean;
}) {
  return (
    <>
      <Link to="/project/$projectId/databases" params={{ projectId }} className="btn-ghost mb-3 -ml-3 shrink-0 text-xs">
        ← {STR.databases}
      </Link>
      {!compact ? (
        <div className="mb-4 shrink-0">
          <span className="block truncate text-sm font-semibold text-ink-100">{databaseName}</span>
          <span className="mt-1 block">
            <IdChip id={databaseIdValue} />
          </span>
        </div>
      ) : null}

      <div className="mb-2 flex shrink-0 items-center justify-between">
        <span className="text-[11px] font-medium tracking-widest text-ink-500 uppercase">{STR.tables}</span>
        <button type="button" className="btn-ghost px-1.5 py-1 text-xs" onClick={onCreate}>
          + Create
        </button>
      </div>

      {/* Only this list scrolls internally when it outgrows the sidebar — the back link,
          database name and "+ Create" row above stay put, same as the page header does. */}
      {tables.length === 0 ? (
        <p className="px-1 py-2 text-xs text-ink-500">No tables yet.</p>
      ) : (
        <nav className="min-h-0 flex-1 space-y-0.5 overflow-y-auto">
          {tables.map((table) => (
            <Link
              key={table.id}
              to="/project/$projectId/databases/$databaseId/tables/$tableId/rows"
              params={{ projectId, databaseId, tableId: table.id }}
              activeOptions={{ exact: false }}
              className="block truncate rounded-lg px-3 py-2 text-sm font-medium text-ink-400 transition-colors hover:bg-ink-850 hover:text-ink-100"
              activeProps={{ className: "bg-ink-800 text-ink-100" }}
            >
              {table.name}
              {!table.enabled ? <span className="ml-1.5 text-xs text-ink-600">(disabled)</span> : null}
            </Link>
          ))}
        </nav>
      )}
    </>
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

function CreateTableModal({
  projectId,
  databaseId,
  onClose,
}: {
  projectId: string;
  databaseId: string;
  onClose: () => void;
}) {
  const create = useCreateTable(projectId, databaseId);
  const navigate = useNavigate();
  const [name, setName] = useState("");
  const [key, setKey] = useState("");
  const [keyTouched, setKeyTouched] = useState(false);
  const error = create.error instanceof ApiError ? create.error : null;

  function slugify(value: string) {
    return value.replace(/[^A-Za-z0-9_]/g, "").slice(0, 64) || "table";
  }

  async function onSubmit(e: FormEvent) {
    e.preventDefault();
    const table = await create.mutateAsync({ key: key || slugify(name), name });
    onClose();
    await navigate({
      to: "/project/$projectId/databases/$databaseId/tables/$tableId/columns",
      params: { projectId, databaseId, tableId: table.id },
    });
  }

  return (
    <Modal title="Create table" onClose={onClose}>
      <form onSubmit={(e) => void onSubmit(e)} className="space-y-4">
        {error && !error.envelope.fields ? <ErrorNote message={error.message} /> : null}
        <Field label="Name" error={error?.fieldErrors("name")[0]}>
          <input
            className="input-base"
            required
            autoFocus
            value={name}
            onChange={(e) => {
              setName(e.target.value);
              if (!keyTouched) setKey(slugify(e.target.value));
            }}
            placeholder="Posts"
          />
        </Field>
        <Field label="Key" error={error?.fieldErrors("key")[0]}>
          <input
            className="input-base font-mono"
            required
            value={key}
            onChange={(e) => {
              setKeyTouched(true);
              setKey(e.target.value);
            }}
            placeholder="posts"
          />
        </Field>
        <button type="submit" className="btn-primary w-full" disabled={create.isPending}>
          {create.isPending ? <Spinner /> : "Create table"}
        </button>
      </form>
    </Modal>
  );
}
