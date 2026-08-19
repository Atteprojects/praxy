import { Link, useNavigate, useParams } from "@tanstack/react-router";
import { useState, type FormEvent } from "react";
import { useCreateDatabase, useDatabases, useDeleteDatabase, useTables } from "../api/databases";
import { ApiError } from "../api/client";
import { useToast } from "../components/toast";
import {
  DataTable, EmptyState, ErrorNote, Field, FullPageSpinner, IdChip, Modal, PageHeader, Spinner,
} from "../components/ui";
import { STR } from "../strings";
import type { Database } from "../api/types";

const HEADERS = ["Name", "Key", "ID", "Created", ""];

export function DatabasesPage() {
  const { projectId } = useParams({ strict: false }) as { projectId: string };
  const databases = useDatabases(projectId);
  const [creating, setCreating] = useState(false);
  const [deleting, setDeleting] = useState<Database | null>(null);

  if (databases.isPending) return <FullPageSpinner />;
  if (databases.isError) throw databases.error;

  return (
    <div>
      <PageHeader
        title={STR.databases}
        description="Each database gets its own isolated Postgres schema. Tables, columns and rows live inside one."
        actions={
          <button type="button" className="btn-primary" onClick={() => setCreating(true)}>
            + Create database
          </button>
        }
      />

      {creating ? <CreateDatabaseModal projectId={projectId} onClose={() => setCreating(false)} /> : null}
      {deleting ? (
        <DeleteDatabaseModal projectId={projectId} database={deleting} onClose={() => setDeleting(null)} />
      ) : null}

      {databases.data.total === 0 ? (
        <EmptyState
          headers={HEADERS}
          title="No databases yet. Each database gets its own isolated schema for tables."
          action={
            <button type="button" className="btn-primary" onClick={() => setCreating(true)}>
              + Create database
            </button>
          }
        />
      ) : (
        <DataTable headers={HEADERS}>
          {databases.data.databases.map((database) => (
            <tr key={database.id}>
              <td className="px-4 py-3">
                <Link
                  to="/project/$projectId/databases/$databaseId"
                  params={{ projectId, databaseId: database.id }}
                  className="block font-medium text-ink-100 hover:text-iris-300"
                >
                  {database.name}
                </Link>
              </td>
              <td className="px-4 py-3 font-mono text-xs text-ink-400">{database.key}</td>
              <td className="px-4 py-3">
                <IdChip id={database.id} />
              </td>
              <td className="px-4 py-3 whitespace-nowrap text-ink-400">
                {new Date(database.createdAt).toLocaleDateString()}
              </td>
              <td className="px-4 py-3 text-right whitespace-nowrap">
                <button
                  type="button"
                  className="btn-ghost border border-ink-700 px-2 py-1 text-xs text-coral-400"
                  onClick={() => setDeleting(database)}
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

function CreateDatabaseModal({ projectId, onClose }: { projectId: string; onClose: () => void }) {
  const create = useCreateDatabase(projectId);
  const navigate = useNavigate();
  const [name, setName] = useState("");
  const [key, setKey] = useState("");
  const [keyTouched, setKeyTouched] = useState(false);
  const error = create.error instanceof ApiError ? create.error : null;

  function slugify(value: string) {
    return value.replace(/[^A-Za-z0-9_]/g, "").slice(0, 64) || "db";
  }

  async function onSubmit(e: FormEvent) {
    e.preventDefault();
    const database = await create.mutateAsync({ key: key || slugify(name), name });
    onClose();
    await navigate({
      to: "/project/$projectId/databases/$databaseId",
      params: { projectId, databaseId: database.id },
    });
  }

  return (
    <Modal title="Create database" onClose={onClose}>
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
            placeholder="Blog"
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
            placeholder="blog"
          />
        </Field>
        <button type="submit" className="btn-primary w-full" disabled={create.isPending}>
          {create.isPending ? <Spinner /> : "Create database"}
        </button>
      </form>
    </Modal>
  );
}

/**
 * Typed-name confirmation, not the one-click `ConfirmButton` used for row-level actions: dropping a
 * database takes every table, column, index and row inside it, which is strictly more than the
 * table danger zone already asks you to type a name for. There is no database settings screen to
 * host a danger-zone section, so the same friction lives in a modal behind the row action.
 */
function DeleteDatabaseModal({
  projectId,
  database,
  onClose,
}: {
  projectId: string;
  database: Database;
  onClose: () => void;
}) {
  const remove = useDeleteDatabase(projectId);
  const tables = useTables(projectId, database.id);
  const [confirmName, setConfirmName] = useState("");
  const toast = useToast();
  const error = remove.error instanceof ApiError ? remove.error : null;

  const tableCount = tables.data?.total;

  async function onDelete() {
    await remove.mutateAsync(database.id);
    onClose();
    toast.success(`Deleted "${database.name}".`);
  }

  return (
    <Modal title="Delete database?" onClose={() => (remove.isPending ? undefined : onClose())}>
      <div className="space-y-4">
        {error ? <ErrorNote message={error.message} /> : null}
        <p className="text-sm text-ink-400">
          Deleting <span className="font-mono text-ink-300">{database.name}</span> drops its entire
          schema —{" "}
          {tableCount === undefined
            ? "every table it contains"
            : tableCount === 0
              ? "it holds no tables"
              : `all ${tableCount} table${tableCount === 1 ? "" : "s"} it contains`}
          , with every column, index and row. This cannot be undone.
        </p>
        <p className="text-xs text-ink-500">
          Type <span className="font-mono text-ink-300">{database.name}</span> to confirm.
        </p>
        <input
          className="input-base"
          autoFocus
          value={confirmName}
          onChange={(e) => setConfirmName(e.target.value)}
          placeholder={database.name}
        />
        <div className="flex justify-end gap-2">
          <button
            type="button"
            className="btn-ghost border border-ink-700"
            disabled={remove.isPending}
            onClick={onClose}
          >
            Cancel
          </button>
          <button
            type="button"
            className="btn-ghost border border-coral-400/60 text-coral-400 disabled:opacity-40"
            disabled={confirmName !== database.name || remove.isPending}
            onClick={() => void onDelete()}
          >
            {remove.isPending ? <Spinner /> : "Delete database"}
          </button>
        </div>
      </div>
    </Modal>
  );
}
