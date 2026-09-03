import { Link, useNavigate } from "@tanstack/react-router";
import { useState, type FormEvent } from "react";
import { ApiError } from "../api/client";
import { useCreateTable, useDatabase, useTables } from "../api/databases";
import { ErrorNote, Field, IdChip, Modal, Spinner } from "../components/ui";
import { STR } from "../strings";

/**
 * The contextual panel that sits beside the icon rail whenever a database is open — the one
 * console-design.md calls for, since switching tables is the highest-frequency navigation here.
 *
 * It lives in `ProjectLayout` rather than `DatabaseLayout` so it can sit flush against the rail,
 * outside the content area's padding and width cap. Rendering it from inside the routed content
 * (as it was) meant a second full-width sidebar indented by the main padding, which cost 432px of
 * chrome before any data.
 */
export function TablesPanel({ projectId, databaseId }: { projectId: string; databaseId: string }) {
  return (
    <aside className="sticky top-14 hidden w-56 shrink-0 flex-col border-r border-ink-800 bg-ink-900/30 px-3 py-4 md:flex">
      <TablesPanelContent projectId={projectId} databaseId={databaseId} />
    </aside>
  );
}

export function TablesPanelContent({
  projectId,
  databaseId,
  compact,
}: {
  projectId: string;
  databaseId: string;
  /** Inside the mobile drawer the sheet header already names the database. */
  compact?: boolean;
}) {
  const database = useDatabase(projectId, databaseId);
  const tables = useTables(projectId, databaseId);
  const [creating, setCreating] = useState(false);

  const sorted = [...(tables.data?.tables ?? [])].sort((a, b) => a.name.localeCompare(b.name));

  return (
    <>
      <Link to="/project/$projectId/databases" params={{ projectId }} className="btn-ghost mb-3 -ml-3 shrink-0 justify-start text-xs">
        ← {STR.databases}
      </Link>

      {!compact ? (
        <div className="mb-4 shrink-0 px-1">
          <span className="block truncate text-sm font-semibold text-ink-100">{database.data?.name ?? "…"}</span>
          {database.data ? (
            <span className="mt-1 block">
              <IdChip id={database.data.id} />
            </span>
          ) : null}
        </div>
      ) : null}

      <div className="mb-2 flex shrink-0 items-center justify-between px-1">
        <span className="text-[11px] font-medium tracking-widest text-ink-500 uppercase">{STR.tables}</span>
        <button type="button" className="btn-ghost px-1.5 py-1 text-xs" onClick={() => setCreating(true)}>
          + Create
        </button>
      </div>

      {/* Only this list scrolls internally when it outgrows the panel — the back link,
          database name and "+ Create" row above stay put. */}
      {tables.isPending ? (
        <p className="px-1 py-2 text-xs text-ink-500">Loading…</p>
      ) : sorted.length === 0 ? (
        <p className="px-1 py-2 text-xs text-ink-500">No tables yet.</p>
      ) : (
        <nav className="min-h-0 flex-1 space-y-0.5 overflow-y-auto">
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

      {creating ? (
        <CreateTableModal projectId={projectId} databaseId={databaseId} onClose={() => setCreating(false)} />
      ) : null}
    </>
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
