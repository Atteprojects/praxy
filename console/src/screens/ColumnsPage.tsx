import { useParams } from "@tanstack/react-router";
import { useState, type FormEvent } from "react";
import {
  useColumns, useCreateColumn, useDeleteColumn, useTable, useTables, useUpdateColumn,
  type CreateColumnInput,
} from "../api/databases";
import { ApiError } from "../api/client";
import { ConfirmButton } from "../components/ConfirmButton";
import { COLUMN_TYPES, type ColumnSchema, type ColumnType } from "../api/types";
import { DataGrid, type DataGridColumn } from "../components/DataGrid";
import { JobStatusBadge } from "../components/JobStatusBadge";
import {
  Badge, EmptyState, ErrorNote, Field, FullPageSpinner, Sheet, Spinner, Toggle,
} from "../components/ui";
import { TableDetailHeader } from "./TableDetailHeader";

const TYPE_LABEL: Record<ColumnType, string> = {
  string: "STR", integer: "INT", float: "FLT", boolean: "BOOL", datetime: "DATE",
  email: "MAIL", url: "URL", ip: "IP", enum: "ENUM", relationship: "REL", geo: "GEO",
};

function TypeIcon({ type }: { type: ColumnType }) {
  return (
    <span className="inline-flex h-6 min-w-11 items-center justify-center rounded-md border border-ink-700 bg-ink-850 px-1.5 font-mono text-[10px] font-semibold tracking-wide text-ink-300">
      {TYPE_LABEL[type]}
    </span>
  );
}

function formatDefault(value: unknown): string {
  if (value === null || value === undefined) return "—";
  if (Array.isArray(value)) return value.length === 0 ? "[]" : JSON.stringify(value);
  return String(value);
}

const HEADERS = ["Key", "Type", "Attributes", "Default", "Status", ""];

export function ColumnsPage() {
  const { projectId, databaseId, tableId } = useParams({ strict: false }) as {
    projectId: string; databaseId: string; tableId: string;
  };
  const table = useTable(projectId, databaseId, tableId);
  const columns = useColumns(projectId, databaseId, tableId);
  const tables = useTables(projectId, databaseId);
  const [creating, setCreating] = useState(false);
  const [editing, setEditing] = useState<ColumnSchema | null>(null);

  if (table.isPending || columns.isPending) return <FullPageSpinner />;
  if (table.isError) throw table.error;
  if (columns.isError) throw columns.error;

  // undefined, not null: an orphaned relationship column omits targetTableId from the JSON entirely.
  const targetTableLabel = (targetTableId: string | undefined) =>
    tables.data?.tables.find((t) => t.id === targetTableId)?.key ?? "…";

  const gridColumns: DataGridColumn<ColumnSchema>[] = [
    {
      accessorKey: "key",
      header: "Key",
      cell: ({ row }) => <span className="font-mono text-sm text-ink-100">{row.original.key}</span>,
    },
    {
      accessorKey: "type",
      header: "Type",
      cell: ({ row }) => <TypeIcon type={row.original.type} />,
    },
    {
      id: "attributes",
      header: "Attributes",
      cell: ({ row }) => (
        <span className="flex flex-wrap gap-1">
          {row.original.required ? <Badge tone="iris">required</Badge> : null}
          {row.original.array ? <Badge>array</Badge> : null}
          {row.original.type === "string" ? <Badge>size {row.original.size}</Badge> : null}
          {row.original.type === "enum" && row.original.elements
            ? <Badge>{row.original.elements.length} values</Badge>
            : null}
          {row.original.type === "relationship"
            ? <Badge>→ {targetTableLabel(row.original.targetTableId)}</Badge>
            : null}
        </span>
      ),
    },
    {
      id: "default",
      header: "Default",
      cell: ({ row }) => (
        <span className={row.original.default == null ? "text-ink-700" : "text-ink-300"}>
          {formatDefault(row.original.default)}
        </span>
      ),
    },
    {
      accessorKey: "status",
      header: "Status",
      cell: ({ row }) => <JobStatusBadge status={row.original.status} error={row.original.error} />,
    },
    {
      id: "actions",
      header: "",
      cell: ({ row }) => (
        <button
          type="button"
          className="btn-ghost border border-ink-700 px-2 py-1 text-xs"
          onClick={(e) => {
            e.stopPropagation();
            setEditing(row.original);
          }}
        >
          Edit
        </button>
      ),
    },
  ];

  return (
    <div>
      <TableDetailHeader projectId={projectId} databaseId={databaseId} table={table.data} active="columns" />

      <div className="mb-4 flex justify-end">
        <button type="button" className="btn-primary" onClick={() => setCreating(true)}>
          + Create column
        </button>
      </div>

      {columns.data.total === 0 ? (
        <EmptyState
          headers={HEADERS}
          title="No columns yet."
          action={
            <button type="button" className="btn-primary" onClick={() => setCreating(true)}>
              + Create column
            </button>
          }
        />
      ) : (
        <DataGrid
          columns={gridColumns}
          data={columns.data.columns}
          getRowId={(c) => c.id}
          onRowClick={(c) => setEditing(c)}
        />
      )}

      {creating ? (
        <CreateColumnSheet
          projectId={projectId}
          databaseId={databaseId}
          tableId={tableId}
          existingKeys={columns.data.columns.map((c) => c.key)}
          onClose={() => setCreating(false)}
        />
      ) : null}

      {editing ? (
        <EditColumnSheet
          projectId={projectId}
          databaseId={databaseId}
          tableId={tableId}
          column={editing}
          onClose={() => setEditing(null)}
        />
      ) : null}
    </div>
  );
}

function CreateColumnSheet({
  projectId,
  databaseId,
  tableId,
  existingKeys,
  onClose,
}: {
  projectId: string;
  databaseId: string;
  tableId: string;
  existingKeys: string[];
  onClose: () => void;
}) {
  const create = useCreateColumn(projectId, databaseId, tableId);
  const tables = useTables(projectId, databaseId);
  const [type, setType] = useState<ColumnType>("string");
  const [key, setKey] = useState("");
  const [required, setRequired] = useState(false);
  const [array, setArray] = useState(false);
  const [size, setSize] = useState(255);
  const [elementsText, setElementsText] = useState("");
  const [defaultText, setDefaultText] = useState("");
  const [targetTableId, setTargetTableId] = useState("");
  const [createMore, setCreateMore] = useState(false);
  const error = create.error instanceof ApiError ? create.error : null;

  function resetForm() {
    setKey("");
    setRequired(false);
    setArray(false);
    setDefaultText("");
    setElementsText("");
    setTargetTableId("");
  }

  function parseDefault(): unknown {
    if (!defaultText.trim()) return undefined;
    if (type === "boolean") return defaultText === "true";
    if (type === "integer") return Number.parseInt(defaultText, 10);
    if (type === "float") return Number.parseFloat(defaultText);
    return array ? defaultText.split(",").map((s) => s.trim()).filter(Boolean) : defaultText;
  }

  async function onSubmit(e: FormEvent) {
    e.preventDefault();
    const input: CreateColumnInput & { type: ColumnType } = {
      type,
      key,
      required,
      array: type === "geo" ? false : array,
      default: type === "relationship" || type === "geo" ? undefined : parseDefault(),
      size: type === "string" ? size : undefined,
      elements: type === "enum" ? elementsText.split(",").map((s) => s.trim()).filter(Boolean) : undefined,
      targetTableId: type === "relationship" ? targetTableId : undefined,
    };
    await create.mutateAsync(input);
    if (createMore) {
      resetForm();
    } else {
      onClose();
    }
  }

  const duplicateKey = key.length > 0 && existingKeys.includes(key);

  return (
    <Sheet
      title="Create column"
      onClose={onClose}
      footer={
        <div className="flex items-center justify-between gap-4">
          <Toggle checked={createMore} onChange={setCreateMore} label="Create more" />
          <button
            type="submit"
            form="create-column-form"
            className="btn-primary"
            disabled={create.isPending || duplicateKey}
          >
            {create.isPending ? <Spinner /> : "Create"}
          </button>
        </div>
      }
    >
      <form id="create-column-form" onSubmit={(e) => void onSubmit(e)} className="space-y-4">
        {error && !error.envelope.fields ? <ErrorNote message={error.message} /> : null}

        <Field label="Type">
          <select
            className="input-base"
            value={type}
            onChange={(e) => setType(e.target.value as ColumnType)}
          >
            {COLUMN_TYPES.map((t) => (
              <option key={t} value={t}>
                {t}
              </option>
            ))}
          </select>
        </Field>

        <Field label="Key" error={duplicateKey ? "A column with this key already exists." : error?.fieldErrors("key")[0]}>
          <input
            className="input-base font-mono"
            required
            autoFocus
            value={key}
            onChange={(e) => setKey(e.target.value)}
            placeholder="title"
          />
        </Field>

        {type === "string" ? (
          <Field label="Size" error={error?.fieldErrors("size")[0]}>
            <input
              className="input-base"
              type="number"
              min={1}
              max={1_000_000}
              required
              value={size}
              onChange={(e) => setSize(Number(e.target.value))}
            />
          </Field>
        ) : null}

        {type === "enum" ? (
          <Field label="Elements (comma-separated)" error={error?.fieldErrors("elements")[0]}>
            <input
              className="input-base"
              required
              value={elementsText}
              onChange={(e) => setElementsText(e.target.value)}
              placeholder="draft, published, archived"
            />
          </Field>
        ) : null}

        {type === "relationship" ? (
          <Field label="Target table" error={error?.fieldErrors("targetTableId")[0]}>
            <select
              className="input-base"
              required
              value={targetTableId}
              onChange={(e) => setTargetTableId(e.target.value)}
            >
              <option value="">— select a table —</option>
              {(tables.data?.tables ?? []).map((t) => (
                <option key={t.id} value={t.id}>{t.name} ({t.key})</option>
              ))}
            </select>
          </Field>
        ) : null}

        <Toggle checked={required} onChange={setRequired} label="Required" />
        {type === "geo" ? null : (
          <Toggle checked={array} onChange={setArray} label="Array" description="Store multiple values as a native Postgres array." />
        )}

        {type === "relationship" || type === "geo" ? null : (
          <Field label="Default (optional)" error={error?.fieldErrors("default")[0]}>
            {type === "boolean" ? (
              <select className="input-base" value={defaultText} onChange={(e) => setDefaultText(e.target.value)}>
                <option value="">— none —</option>
                <option value="true">true</option>
                <option value="false">false</option>
              </select>
            ) : (
              <input
                className="input-base"
                type={type === "integer" || type === "float" ? "number" : "text"}
                value={defaultText}
                onChange={(e) => setDefaultText(e.target.value)}
                placeholder={array ? "comma-separated values" : undefined}
              />
            )}
          </Field>
        )}
      </form>
    </Sheet>
  );
}

function EditColumnSheet({
  projectId,
  databaseId,
  tableId,
  column,
  onClose,
}: {
  projectId: string;
  databaseId: string;
  tableId: string;
  column: ColumnSchema;
  onClose: () => void;
}) {
  const update = useUpdateColumn(projectId, databaseId, tableId, column.id);
  const remove = useDeleteColumn(projectId, databaseId, tableId);
  const [key, setKey] = useState(column.key);
  const [required, setRequired] = useState(column.required);
  const error = update.error instanceof ApiError ? update.error : null;

  async function onSubmit(e: FormEvent) {
    e.preventDefault();
    await update.mutateAsync({ key, required });
    onClose();
  }

  async function onDelete() {
    await remove.mutateAsync({ columnId: column.id, force: column.required });
    onClose();
  }

  return (
    <Sheet
      title="Edit column"
      onClose={onClose}
      footer={
        <button type="submit" form="edit-column-form" className="btn-primary w-full" disabled={update.isPending}>
          {update.isPending ? <Spinner /> : "Save"}
        </button>
      }
    >
      <form id="edit-column-form" onSubmit={(e) => void onSubmit(e)} className="space-y-4">
        {error && !error.envelope.fields ? <ErrorNote message={error.message} /> : null}

        <Field label="Type">
          <input className="input-base font-mono opacity-60" value={column.type} disabled />
        </Field>

        <Field label="Key" error={error?.fieldErrors("key")[0]}>
          <input
            className="input-base font-mono"
            required
            autoFocus
            value={key}
            onChange={(e) => setKey(e.target.value)}
          />
        </Field>

        <Toggle checked={required} onChange={setRequired} label="Required" />
      </form>

      <div className="mt-8 border-t border-coral-400/20 pt-5">
        <h3 className="mb-2 text-sm font-medium text-coral-400">Danger zone</h3>
        <ConfirmButton
          label="Delete column"
          title="Delete column?"
          confirmLabel="Delete column"
          successMessage={`Deleted "${column.key}".`}
          className="btn-ghost mt-2 border border-ink-700 text-coral-400"
          body={
            <>
              Dropping <span className="font-mono text-ink-300">{column.key}</span> deletes its value in
              every row of this table. This cannot be undone.
              {column.required ? " It is a required column, so existing rows depend on it." : ""}
            </>
          }
          onConfirm={onDelete}
        />
      </div>
    </Sheet>
  );
}
