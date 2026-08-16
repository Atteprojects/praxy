import { Link, Outlet, useNavigate, useParams } from "@tanstack/react-router";
import { useState, type FormEvent, type ReactNode } from "react";
import { useCreateTable, useDatabase, useTables } from "../api/databases";
import { ApiError } from "../api/client";
import { ErrorNote, Field, FullPageSpinner, IdChip, Modal, Spinner } from "../components/ui";
import { STR } from "../strings";

/**
 * The exception to the tabs pattern (console-design.md): a second sidebar listing tables, because
 * switching tables is the highest-frequency navigation in the whole console.
 */
export function DatabaseLayout() {
  const { projectId, databaseId } = useParams({ strict: false }) as { projectId: string; databaseId: string };
  const database = useDatabase(projectId, databaseId);
  const tables = useTables(projectId, databaseId);
  const [creating, setCreating] = useState(false);

  if (database.isPending || tables.isPending) return <FullPageSpinner />;
  if (database.isError) throw database.error;
  if (tables.isError) throw tables.error;

  const sorted = [...tables.data.tables].sort((a, b) => a.name.localeCompare(b.name));

  return (
    <div className="flex min-h-dvh gap-8">
      <aside className="sticky top-14 max-h-[calc(100dvh-3.5rem)] w-56 shrink-0 overflow-y-auto">
        <Link to="/project/$projectId/databases" params={{ projectId }} className="btn-ghost mb-3 -ml-3 text-xs">
          ← {STR.databases}
        </Link>
        <div className="mb-4">
          <span className="block truncate text-sm font-semibold text-ink-100">{database.data.name}</span>
          <span className="mt-1 block">
            <IdChip id={database.data.id} />
          </span>
        </div>

        <div className="mb-2 flex items-center justify-between">
          <span className="text-[11px] font-medium tracking-widest text-ink-500 uppercase">{STR.tables}</span>
          <button type="button" className="btn-ghost px-1.5 py-1 text-xs" onClick={() => setCreating(true)}>
            + Create
          </button>
        </div>

        {sorted.length === 0 ? (
          <p className="px-1 py-2 text-xs text-ink-500">No tables yet.</p>
        ) : (
          <nav className="space-y-0.5">
            {sorted.map((table) => (
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
      </aside>

      <div className="min-w-0 flex-1">
        <Outlet />
      </div>

      {creating ? (
        <CreateTableModal projectId={projectId} databaseId={databaseId} onClose={() => setCreating(false)} />
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
