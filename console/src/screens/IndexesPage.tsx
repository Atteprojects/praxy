import { useParams } from "@tanstack/react-router";
import { useState, type FormEvent } from "react";
import {
  useCancelSchemaJob, useColumns, useCreateIndex, useDeleteIndex, useIndexes, useRetrySchemaJob, useSchemaJobs,
  useTable,
} from "../api/databases";
import { ApiError } from "../api/client";
import { ConfirmButton } from "../components/ConfirmButton";
import { INDEX_TYPES, type IndexSchema, type IndexType } from "../api/types";
import { DataGrid, type DataGridColumn } from "../components/DataGrid";
import { JobStatusBadge } from "../components/JobStatusBadge";
import { Badge, EmptyState, ErrorNote, Field, FullPageSpinner, Sheet, Spinner } from "../components/ui";
import { STR } from "../strings";
import { TableDetailHeader } from "./TableDetailHeader";

const TYPE_TONE = { key: "ink", unique: "iris", fulltext: "mint", spatial: "amber" } as const;

const HEADERS = ["Key", "Type", "Columns", "Status", ""];

export function IndexesPage() {
  const { projectId, databaseId, tableId } = useParams({ strict: false }) as {
    projectId: string; databaseId: string; tableId: string;
  };
  const table = useTable(projectId, databaseId, tableId);
  const columns = useColumns(projectId, databaseId, tableId);
  const indexes = useIndexes(projectId, databaseId, tableId);
  const jobs = useSchemaJobs(projectId, databaseId, tableId);
  const cancelJob = useCancelSchemaJob(projectId, databaseId, tableId);
  const retryJob = useRetrySchemaJob(projectId, databaseId, tableId);
  const [creating, setCreating] = useState(false);
  const deleteIndex = useDeleteIndex(projectId, databaseId, tableId);

  if (table.isPending || columns.isPending || indexes.isPending) return <FullPageSpinner />;
  if (table.isError) throw table.error;
  if (columns.isError) throw columns.error;
  if (indexes.isError) throw indexes.error;

  const jobByIndexId = new Map((jobs.data?.jobs ?? []).map((j) => [j.indexId, j]));

  const gridColumns: DataGridColumn<IndexSchema>[] = [
    {
      accessorKey: "key",
      header: "Key",
      cell: ({ row }) => <span className="font-mono text-sm text-ink-100">{row.original.key}</span>,
    },
    {
      accessorKey: "type",
      header: "Type",
      cell: ({ row }) => <Badge tone={TYPE_TONE[row.original.type]}>{row.original.type}</Badge>,
    },
    {
      id: "columns",
      header: "Columns",
      cell: ({ row }) => (
        <span className="flex flex-wrap gap-1">
          {row.original.columns.map((c, i) => (
            <Badge key={c}>
              {c}
              {row.original.orders[i] === "desc"
                ? " ↓"
                : row.original.type !== "fulltext" && row.original.type !== "spatial" ? " ↑" : ""}
            </Badge>
          ))}
        </span>
      ),
    },
    {
      id: "status",
      header: "Status",
      cell: ({ row }) => {
        const job = jobByIndexId.get(row.original.id);
        return (
          <JobStatusBadge
            status={row.original.status}
            error={row.original.error}
            startedAt={job?.startedAt}
            busy={cancelJob.isPending || retryJob.isPending}
            onCancel={job ? () => cancelJob.mutate(job.id) : undefined}
            onRetry={job ? () => retryJob.mutate(job.id) : undefined}
          />
        );
      },
    },
    {
      id: "actions",
      header: "",
      cell: ({ row }) => (
        <span onClick={(e) => e.stopPropagation()}>
          <ConfirmButton
            label="Delete"
            title="Drop index?"
            confirmLabel="Drop index"
            successMessage={`Dropped "${row.original.key}".`}
            body={
              <>
                Dropping <span className="font-mono text-ink-300">{row.original.key}</span> can slow queries that
                relied on it. Rebuilding a large index later is a background job, not an instant operation.
              </>
            }
            onConfirm={() => deleteIndex.mutateAsync(row.original.id)}
          />
        </span>
      ),
    },
  ];

  return (
    <div>
      <TableDetailHeader projectId={projectId} databaseId={databaseId} table={table.data} active="indexes" />

      <div className="mb-4 flex justify-end">
        <button
          type="button"
          className="btn-primary"
          disabled={columns.data.total === 0}
          onClick={() => setCreating(true)}
        >
          + Create index
        </button>
      </div>
      {columns.data.total === 0 ? (
        <p className="mb-4 text-xs text-ink-500">Add at least one column before creating an index.</p>
      ) : null}

      {indexes.data.total === 0 ? (
        <EmptyState
          headers={HEADERS}
          title="No indexes yet. Unique and fulltext indexes build in the background — watch the status column."
          action={
            <button
              type="button"
              className="btn-primary"
              disabled={columns.data.total === 0}
              onClick={() => setCreating(true)}
            >
              + Create index
            </button>
          }
        />
      ) : (
        <DataGrid columns={gridColumns} data={indexes.data.indexes} getRowId={(i) => i.id} />
      )}

      {creating ? (
        <CreateIndexSheet
          projectId={projectId}
          databaseId={databaseId}
          tableId={tableId}
          columnKeys={columns.data.columns.map((c) => c.key)}
          onClose={() => setCreating(false)}
        />
      ) : null}
    </div>
  );
}

function CreateIndexSheet({
  projectId,
  databaseId,
  tableId,
  columnKeys,
  onClose,
}: {
  projectId: string;
  databaseId: string;
  tableId: string;
  columnKeys: string[];
  onClose: () => void;
}) {
  const create = useCreateIndex(projectId, databaseId, tableId);
  const [key, setKey] = useState("");
  const [type, setType] = useState<IndexType>("key");
  const [selected, setSelected] = useState<string[]>([]);
  const [orders, setOrders] = useState<Record<string, "asc" | "desc">>({});
  const error = create.error instanceof ApiError ? create.error : null;

  function toggleColumn(col: string) {
    setSelected((current) =>
      current.includes(col) ? current.filter((c) => c !== col) : [...current, col]);
  }

  async function onSubmit(e: FormEvent) {
    e.preventDefault();
    await create.mutateAsync({
      key,
      type,
      columns: selected,
      orders: type === "fulltext" || type === "spatial" ? undefined : selected.map((c) => orders[c] ?? "asc"),
    });
    onClose();
  }

  return (
    <Sheet
      title="Create index"
      onClose={onClose}
      footer={
        <button
          type="submit"
          form="create-index-form"
          className="btn-primary w-full"
          disabled={create.isPending || selected.length === 0}
        >
          {create.isPending ? <Spinner /> : "Create index"}
        </button>
      }
    >
      <form id="create-index-form" onSubmit={(e) => void onSubmit(e)} className="space-y-4">
        {error && !error.envelope.fields ? <ErrorNote message={error.message} /> : null}

        <Field label="Key" error={error?.fieldErrors("key")[0]}>
          <input
            className="input-base font-mono"
            required
            autoFocus
            value={key}
            onChange={(e) => setKey(e.target.value)}
            placeholder="idx_title"
          />
        </Field>

        <Field label="Type">
          <select className="input-base" value={type} onChange={(e) => setType(e.target.value as IndexType)}>
            {INDEX_TYPES.map((t) => (
              <option key={t} value={t}>
                {t}
              </option>
            ))}
          </select>
        </Field>

        <div>
          <span className="mb-1.5 block text-xs font-medium tracking-wide text-ink-400 uppercase">{STR.columns}</span>
          <div className="space-y-1.5">
            {columnKeys.map((col) => (
              <label
                key={col}
                className={`flex cursor-pointer items-center justify-between gap-2 rounded-lg border px-3 py-2 text-sm transition-colors ${
                  selected.includes(col)
                    ? "border-iris-500/60 bg-iris-500/10 text-ink-100"
                    : "border-ink-700 text-ink-400 hover:border-ink-500"
                }`}
              >
                <span className="flex items-center gap-2">
                  <input
                    type="checkbox"
                    className="accent-iris-500"
                    checked={selected.includes(col)}
                    onChange={() => toggleColumn(col)}
                  />
                  <span className="font-mono text-xs">{col}</span>
                </span>
                {type !== "fulltext" && type !== "spatial" && selected.includes(col) ? (
                  <select
                    className="rounded border border-ink-700 bg-ink-950 px-1.5 py-0.5 text-xs"
                    value={orders[col] ?? "asc"}
                    onChange={(e) => setOrders((o) => ({ ...o, [col]: e.target.value as "asc" | "desc" }))}
                    onClick={(e) => e.stopPropagation()}
                  >
                    <option value="asc">asc</option>
                    <option value="desc">desc</option>
                  </select>
                ) : null}
              </label>
            ))}
          </div>
          {error?.fieldErrors("columns")[0] ? (
            <span className="mt-1 block text-xs text-coral-400">{error.fieldErrors("columns")[0]}</span>
          ) : null}
        </div>
      </form>
    </Sheet>
  );
}
