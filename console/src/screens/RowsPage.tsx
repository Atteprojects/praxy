import { useParams } from "@tanstack/react-router";
import { useEffect, useMemo, useRef, useState, type FormEvent, type ReactNode } from "react";
import { useColumns, useTable } from "../api/databases";
import { ApiError } from "../api/client";
import {
  useBulkDeleteRows, useCreateRow, useDeleteRow, useRows, useUpdateRow, type SortState,
} from "../api/rows";
import type { ColumnSchema, ColumnType, QueryFilter, Row } from "../api/types";
import { ConfirmButton } from "../components/ConfirmButton";
import { DataGrid, type DataGridColumn } from "../components/DataGrid";
import { AddRoleButton, RoleLabel } from "../components/RolePicker";
import { RelationshipValueEditor } from "../components/RelationshipPicker";
import { EmptyState, ErrorNote, Field, FullPageSpinner, Sheet, Spinner } from "../components/ui";
import { TableDetailHeader } from "./TableDetailHeader";

// relationship's entry is the array-column superset (adds "contains") — describeFilter only needs
// it for label lookup; FilterPicker below re-derives the array-conditioned list per column
// instance, since eligibility depends on that column's own `array` flag, not just its type.
const OPERATORS_BY_TYPE: Partial<Record<ColumnType, { value: string; label: string; arity: 0 | 1 | 2 }[]>> = {
  string: textOps(), email: textOps(), url: textOps(), ip: textOps(), enum: equalityOps(),
  integer: numericOps(), float: numericOps(), datetime: numericOps(),
  boolean: equalityOps(),
  relationship: relationshipOps(true),
};

function equalityOps() {
  return [
    { value: "equal", label: "=", arity: 1 as const },
    { value: "notEqual", label: "≠", arity: 1 as const },
    { value: "isNull", label: "is NULL", arity: 0 as const },
    { value: "isNotNull", label: "is not NULL", arity: 0 as const },
  ];
}
function numericOps() {
  return [
    ...equalityOps(),
    { value: "lessThan", label: "<", arity: 1 as const },
    { value: "lessThanEqual", label: "≤", arity: 1 as const },
    { value: "greaterThan", label: ">", arity: 1 as const },
    { value: "greaterThanEqual", label: "≥", arity: 1 as const },
  ];
}
function textOps() {
  return [
    ...numericOps(),
    { value: "startsWith", label: "starts with", arity: 1 as const },
    { value: "endsWith", label: "ends with", arity: 1 as const },
    { value: "contains", label: "contains", arity: 1 as const },
  ];
}
/** Never startsWith/endsWith (meaningless on uuids); contains only makes sense for the array case. */
function relationshipOps(array: boolean) {
  return array ? [...equalityOps(), { value: "contains", label: "contains", arity: 1 as const }] : equalityOps();
}

function readFiltersFromUrl(): QueryFilter[] {
  try {
    const raw = new URLSearchParams(window.location.search).get("query");
    if (!raw) return [];
    const parsed = JSON.parse(raw);
    return Array.isArray(parsed) ? parsed : [];
  } catch {
    return [];
  }
}

function writeFiltersToUrl(filters: QueryFilter[]) {
  const url = new URL(window.location.href);
  if (filters.length === 0) url.searchParams.delete("query");
  else url.searchParams.set("query", JSON.stringify(filters));
  window.history.replaceState(null, "", url.toString());
}

function describeFilter(f: QueryFilter): string {
  const ops = Object.values(OPERATORS_BY_TYPE).flatMap((o) => o ?? []);
  const label = ops.find((o) => o.value === f.method)?.label ?? f.method;
  const value = f.values && f.values.length > 0 ? ` ${f.values.map(String).join(", ")}` : "";
  return `${f.attribute}${f.attribute ? " " : ""}${label}${value}`;
}

// An expanded relationship value (?expand=) is an object (or array of objects) in place of the
// raw id string(s) the grid previously always saw — the one place that assumption broke.
function relationshipPreview(value: unknown): string {
  const id = (value as Record<string, unknown> | null)?.$id;
  return typeof id === "string" ? `#${id.slice(0, 8)}` : String(value);
}

function formatCell(value: unknown): ReactNode {
  if (value === null || value === undefined) return <span className="text-ink-600 italic">NULL</span>;
  if (typeof value === "boolean") return <span className={value ? "text-mint-400" : "text-coral-400"}>{String(value)}</span>;
  if (Array.isArray(value)) {
    return value.length === 0
      ? <span className="text-ink-600">[]</span>
      : value.map((v) => (v && typeof v === "object" ? relationshipPreview(v) : String(v))).join(", ");
  }
  if (value && typeof value === "object") return relationshipPreview(value);
  return String(value);
}

/** One editable cell: click to edit, Enter/blur saves only this field, Escape cancels. */
function EditableCell({
  column, value, onSave, projectId, databaseId,
}: {
  column: ColumnSchema;
  value: unknown;
  onSave: (value: unknown) => void;
  projectId: string;
  databaseId: string;
}) {
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState("");

  // No display-field concept exists (docs/research/table-relationships.md) — the picker searches by
  // $id prefix. targetTableId is null only when Phase 2 orphaned the column (its target table was
  // force-deleted); there's nothing left to search, so that case falls through to the plain text
  // input below instead, same as Phase 1's original behavior.
  if (column.type === "relationship" && column.targetTableId) {
    const ids = column.array ? (Array.isArray(value) ? value : []) : value === null || value === undefined ? [] : [value];
    return (
      <RelationshipValueEditor
        ids={ids}
        array={column.array}
        targetTableId={column.targetTableId}
        projectId={projectId}
        databaseId={databaseId}
        onChange={(next) => onSave(column.array ? next : (next[0] ?? null))}
      />
    );
  }

  function begin() {
    setDraft(column.array ? (Array.isArray(value) ? value.join(", ") : "") : value === null || value === undefined ? "" : String(value));
    setEditing(true);
  }

  function parse(): unknown {
    if (draft === "") return null;
    if (column.array) {
      const parts = draft.split(",").map((s) => s.trim()).filter(Boolean);
      return parts.map((p) => parseScalar(column.type, p));
    }
    return parseScalar(column.type, draft);
  }

  function commit() {
    setEditing(false);
    const parsed = parse();
    if (JSON.stringify(parsed) !== JSON.stringify(value ?? null)) onSave(parsed);
  }

  if (!editing) {
    if (column.type === "boolean" && !column.array) {
      return (
        <button
          type="button"
          className="block w-full text-left"
          onClick={(e) => { e.stopPropagation(); begin(); }}
        >
          {formatCell(value)}
        </button>
      );
    }
    return (
      <button
        type="button"
        className="block w-full truncate text-left"
        onClick={(e) => { e.stopPropagation(); begin(); }}
        title="Click to edit"
      >
        {formatCell(value)}
      </button>
    );
  }

  if (column.type === "boolean" && !column.array) {
    return (
      <select
        autoFocus
        className="input-base py-1 text-sm"
        defaultValue={value === null || value === undefined ? "" : String(value)}
        onClick={(e) => e.stopPropagation()}
        onChange={(e) => {
          setEditing(false);
          const v = e.target.value === "" ? null : e.target.value === "true";
          if (v !== (value ?? null)) onSave(v);
        }}
        onBlur={() => setEditing(false)}
      >
        <option value="">NULL</option>
        <option value="true">true</option>
        <option value="false">false</option>
      </select>
    );
  }

  if (column.type === "enum" && !column.array) {
    return (
      <select
        autoFocus
        className="input-base py-1 text-sm"
        defaultValue={value === null || value === undefined ? "" : String(value)}
        onClick={(e) => e.stopPropagation()}
        onChange={(e) => {
          setEditing(false);
          const v = e.target.value === "" ? null : e.target.value;
          if (v !== (value ?? null)) onSave(v);
        }}
        onBlur={() => setEditing(false)}
      >
        <option value="">NULL</option>
        {(column.elements ?? []).map((el) => (
          <option key={el} value={el}>{el}</option>
        ))}
      </select>
    );
  }

  return (
    <input
      autoFocus
      className="input-base py-1 text-sm"
      value={draft}
      onClick={(e) => e.stopPropagation()}
      onChange={(e) => setDraft(e.target.value)}
      onBlur={commit}
      onKeyDown={(e) => {
        if (e.key === "Enter") { e.preventDefault(); commit(); }
        if (e.key === "Escape") setEditing(false);
      }}
    />
  );
}

function parseScalar(type: ColumnType, raw: string): unknown {
  if (type === "integer") return Number.parseInt(raw, 10);
  if (type === "float") return Number.parseFloat(raw);
  return raw;
}

export function RowsPage() {
  const { projectId, databaseId, tableId } = useParams({ strict: false }) as {
    projectId: string; databaseId: string; tableId: string;
  };
  const table = useTable(projectId, databaseId, tableId);
  const columns = useColumns(projectId, databaseId, tableId);
  // Always expand relationship columns for display — RowSheet's raw JSON and the grid's cell
  // preview both benefit, and it's the only way the owner can see a linked row's data without a
  // display-field concept (docs/research/table-relationships.md's explicit non-goal).
  const relationshipKeys = columns.data?.columns.filter((c) => c.type === "relationship").map((c) => c.key) ?? [];
  const [filters, setFilters] = useState<QueryFilter[]>(() => readFiltersFromUrl());
  const [sort, setSort] = useState<SortState | null>(null);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [filterPickerOpen, setFilterPickerOpen] = useState(false);
  const [creating, setCreating] = useState(false);
  const [sheetRowId, setSheetRowId] = useState<string | null>(
    () => new URLSearchParams(window.location.search).get("row"),
  );

  const rows = useRows(projectId, databaseId, tableId, filters, sort, relationshipKeys);
  const updateRow = useUpdateRow(projectId, databaseId, tableId);
  const deleteRow = useDeleteRow(projectId, databaseId, tableId);
  const bulkDelete = useBulkDeleteRows(projectId, databaseId, tableId);

  useEffect(() => writeFiltersToUrl(filters), [filters]);

  const flatRows = useMemo(() => rows.data?.pages.flatMap((p) => p.rows) ?? [], [rows.data]);
  const total = rows.data?.pages[0]?.total ?? null;

  if (table.isPending || columns.isPending) return <FullPageSpinner />;
  if (table.isError) throw table.error;
  if (columns.isError) throw columns.error;
  if (rows.isError) throw rows.error;

  const cols = columns.data.columns;
  const headers = ["", "$id", ...cols.map((c) => c.key), ""];

  function toggleSort(key: string) {
    setSort((current) => {
      if (current?.attribute !== key) return { attribute: key, direction: "asc" };
      if (current.direction === "asc") return { attribute: key, direction: "desc" };
      return null;
    });
  }

  function toggleSelected(id: string) {
    setSelected((cur) => {
      const next = new Set(cur);
      if (next.has(id)) next.delete(id); else next.add(id);
      return next;
    });
  }

  const gridColumns: DataGridColumn<Row>[] = [
    {
      id: "__select",
      header: () => (
        <input
          type="checkbox"
          className="accent-iris-500"
          checked={flatRows.length > 0 && selected.size === flatRows.length}
          onChange={(e) => setSelected(e.target.checked ? new Set(flatRows.map((r) => r.$id)) : new Set())}
        />
      ),
      cell: ({ row }) => (
        <input
          type="checkbox"
          className="accent-iris-500"
          checked={selected.has(row.original.$id)}
          onChange={() => toggleSelected(row.original.$id)}
          onClick={(e) => e.stopPropagation()}
        />
      ),
    },
    {
      id: "$id",
      header: () => (
        <button type="button" className="flex items-center gap-1 uppercase" onClick={() => toggleSort("$id")}>
          $id {sort?.attribute === "$id" ? (sort.direction === "asc" ? "↑" : "↓") : null}
        </button>
      ),
      cell: ({ row }) => <span className="font-mono text-xs text-ink-500">{row.original.$id.slice(0, 12)}…</span>,
    },
    ...cols.map((column): DataGridColumn<Row> => ({
      id: column.key,
      header: () => (
        <button type="button" className="flex items-center gap-1 uppercase" onClick={() => toggleSort(column.key)}>
          {column.key} {sort?.attribute === column.key ? (sort.direction === "asc" ? "↑" : "↓") : null}
        </button>
      ),
      cell: ({ row }) => (
        <EditableCell
          column={column}
          value={row.original[column.key]}
          projectId={projectId}
          databaseId={databaseId}
          onSave={(value) =>
            updateRow.mutate({ rowId: row.original.$id, data: { [column.key]: value } })
          }
        />
      ),
    })),
    {
      id: "__actions",
      header: "",
      cell: ({ row }) => (
        <button
          type="button"
          className="btn-ghost border border-ink-700 px-2 py-1 text-xs"
          onClick={(e) => { e.stopPropagation(); setSheetRowId(row.original.$id); }}
        >
          Open
        </button>
      ),
    },
  ];

  return (
    <div>
      <TableDetailHeader projectId={projectId} databaseId={databaseId} table={table.data} active="rows" />

      <div className="mb-4 flex flex-wrap items-center justify-between gap-3">
        <div className="flex flex-wrap items-center gap-2">
          <div className="relative">
            <button type="button" className="btn-ghost border border-ink-700 text-xs" onClick={() => setFilterPickerOpen((v) => !v)}>
              + Filter
            </button>
            {filterPickerOpen ? (
              <FilterPicker
                columns={cols}
                onAdd={(f) => { setFilters((cur) => [...cur, f]); setFilterPickerOpen(false); }}
                onClose={() => setFilterPickerOpen(false)}
              />
            ) : null}
          </div>
          {filters.map((f, i) => (
            <span key={i} className="inline-flex items-center gap-1.5 rounded-md border border-ink-700 bg-ink-850 px-2 py-1 font-mono text-xs text-ink-300">
              {describeFilter(f)}
              <button type="button" className="text-ink-500 hover:text-coral-400" onClick={() => setFilters((cur) => cur.filter((_, idx) => idx !== i))}>
                ✕
              </button>
            </span>
          ))}
          {filters.length > 0 ? (
            <button type="button" className="text-xs text-ink-500 hover:text-ink-300" onClick={() => setFilters([])}>
              Clear filters
            </button>
          ) : null}
        </div>
        <div className="flex items-center gap-3">
          {total !== null ? <span className="text-xs text-ink-500">{total} total</span> : null}
          <button type="button" className="btn-primary" onClick={() => setCreating(true)}>
            + Create row
          </button>
        </div>
      </div>

      {selected.size > 0 ? (
        <div className="mb-4 flex items-center justify-between rounded-lg border border-iris-500/40 bg-iris-500/10 px-4 py-2.5 text-sm">
          <span className="text-ink-200">{selected.size} selected</span>
          <div className="flex items-center gap-3">
            <button type="button" className="text-ink-400 hover:text-ink-200" onClick={() => setSelected(new Set())}>
              Cancel
            </button>
            <button
              type="button"
              className="text-coral-400 hover:text-coral-300"
              disabled={bulkDelete.isPending}
              onClick={async () => {
                await bulkDelete.mutateAsync([...selected]);
                setSelected(new Set());
              }}
            >
              {bulkDelete.isPending ? <Spinner className="inline size-3.5" /> : "Delete"}
            </button>
          </div>
        </div>
      ) : null}

      {flatRows.length === 0 ? (
        filters.length > 0 ? (
          <EmptyState
            headers={headers}
            title="No rows match your filters."
            action={
              <button type="button" className="btn-ghost border border-ink-700" onClick={() => setFilters([])}>
                Clear filters
              </button>
            }
          />
        ) : (
          <EmptyState
            headers={headers}
            title="No rows yet."
            action={
              <button type="button" className="btn-primary" onClick={() => setCreating(true)}>
                + Create row
              </button>
            }
          />
        )
      ) : (
        <DataGrid
          columns={gridColumns}
          data={flatRows}
          getRowId={(r) => r.$id}
          onRowClick={(r) => setSheetRowId(r.$id)}
          onNearEnd={() => {
            if (rows.hasNextPage && !rows.isFetchingNextPage) void rows.fetchNextPage();
          }}
        />
      )}
      {rows.isFetchingNextPage ? (
        <p className="mt-3 text-center text-xs text-ink-500"><Spinner className="mr-1 inline size-3" />Loading more…</p>
      ) : null}

      {creating ? (
        <CreateRowSheet projectId={projectId} databaseId={databaseId} tableId={tableId} columns={cols} onClose={() => setCreating(false)} />
      ) : null}

      {sheetRowId ? (
        <RowSheet
          projectId={projectId}
          databaseId={databaseId}
          tableId={tableId}
          rowId={sheetRowId}
          rows={flatRows}
          rowSecurity={table.data.rowSecurity}
          onNavigate={setSheetRowId}
          onClose={() => setSheetRowId(null)}
          onDelete={async (id) => { await deleteRow.mutateAsync(id); setSheetRowId(null); }}
        />
      ) : null}
    </div>
  );
}

function FilterPicker({
  columns, onAdd, onClose,
}: {
  columns: ColumnSchema[];
  onAdd: (f: QueryFilter) => void;
  onClose: () => void;
}) {
  const [attribute, setAttribute] = useState(columns[0]?.key ?? "");
  const [method, setMethod] = useState("equal");
  const [value, setValue] = useState("");
  const column = columns.find((c) => c.key === attribute);
  const ops = column ? (column.type === "relationship" ? relationshipOps(column.array) : (OPERATORS_BY_TYPE[column.type] ?? [])) : [];
  const op = ops.find((o) => o.value === method) ?? ops[0];

  useEffect(() => {
    if (column && !ops.some((o) => o.value === method)) setMethod(ops[0]?.value ?? "equal");
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [attribute]);

  function submit() {
    if (!column || !op) return;
    const values = op.arity === 0 ? undefined : [column.type === "integer" || column.type === "float" ? Number(value) : value];
    onAdd({ method: op.value, attribute: column.key, values });
  }

  return (
    <div className="absolute top-full left-0 z-20 mt-1.5 w-72 space-y-3 rounded-lg border border-ink-700 bg-ink-900 p-3 shadow-xl shadow-black/40">
      <select className="input-base text-xs" value={attribute} onChange={(e) => setAttribute(e.target.value)}>
        {columns.map((c) => <option key={c.key} value={c.key}>{c.key}</option>)}
      </select>
      <select className="input-base text-xs" value={method} onChange={(e) => setMethod(e.target.value)}>
        {ops.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
      </select>
      {op?.arity === 1 ? (
        <input className="input-base text-xs" value={value} onChange={(e) => setValue(e.target.value)} placeholder="value" />
      ) : null}
      <div className="flex justify-end gap-2">
        <button type="button" className="btn-ghost px-2 py-1 text-xs" onClick={onClose}>Cancel</button>
        <button type="button" className="btn-primary px-2 py-1 text-xs" onClick={() => { submit(); onClose(); }}>Add filter</button>
      </div>
    </div>
  );
}

function CreateRowSheet({
  projectId, databaseId, tableId, columns, onClose,
}: {
  projectId: string; databaseId: string; tableId: string; columns: ColumnSchema[]; onClose: () => void;
}) {
  const create = useCreateRow(projectId, databaseId, tableId);
  const [values, setValues] = useState<Record<string, string>>({});
  const error = create.error instanceof ApiError ? create.error : null;

  async function onSubmit(e: FormEvent) {
    e.preventDefault();
    const data: Record<string, unknown> = {};
    for (const column of columns) {
      const raw = values[column.key];
      if (raw === undefined || raw === "") continue;
      data[column.key] = column.array
        ? raw.split(",").map((s) => parseScalar(column.type, s.trim())).filter((v) => v !== "")
        : column.type === "boolean" ? raw === "true" : parseScalar(column.type, raw);
    }
    await create.mutateAsync({ data });
    onClose();
  }

  return (
    <Sheet
      title="Create row"
      size="lg"
      onClose={onClose}
      footer={
        <button type="submit" form="create-row-form" className="btn-primary w-full" disabled={create.isPending}>
          {create.isPending ? <Spinner /> : "Create"}
        </button>
      }
    >
      <form id="create-row-form" onSubmit={(e) => void onSubmit(e)} className="space-y-4">
        {error && !error.envelope.fields ? <ErrorNote message={error.message} /> : null}
        {columns.length === 0 ? <p className="text-sm text-ink-500">Add a column first.</p> : null}
        {columns.map((column) => (
          <Field key={column.id} label={`${column.key}${column.required ? " *" : ""}`} error={error?.fieldErrors(column.key)[0]}>
            {column.type === "relationship" && column.targetTableId ? (
              <RelationshipValueEditor
                ids={values[column.key] ? values[column.key].split(",").map((s) => s.trim()).filter(Boolean) : []}
                array={column.array}
                targetTableId={column.targetTableId}
                projectId={projectId}
                databaseId={databaseId}
                onChange={(next) => setValues((v) => ({ ...v, [column.key]: next.join(",") }))}
              />
            ) : column.type === "boolean" ? (
              <select
                className="input-base"
                value={values[column.key] ?? ""}
                onChange={(e) => setValues((v) => ({ ...v, [column.key]: e.target.value }))}
              >
                <option value="">— unset —</option>
                <option value="true">true</option>
                <option value="false">false</option>
              </select>
            ) : column.type === "enum" && !column.array ? (
              <select
                className="input-base"
                value={values[column.key] ?? ""}
                onChange={(e) => setValues((v) => ({ ...v, [column.key]: e.target.value }))}
              >
                <option value="">— unset —</option>
                {(column.elements ?? []).map((el) => <option key={el} value={el}>{el}</option>)}
              </select>
            ) : (
              <input
                className="input-base font-mono"
                type={column.type === "integer" || column.type === "float" ? "number" : "text"}
                value={values[column.key] ?? ""}
                onChange={(e) => setValues((v) => ({ ...v, [column.key]: e.target.value }))}
                placeholder={column.array ? "comma-separated values" : undefined}
              />
            )}
          </Field>
        ))}
      </form>
    </Sheet>
  );
}

function RowSheet({
  projectId, databaseId, tableId, rowId, rows, rowSecurity, onNavigate, onClose, onDelete,
}: {
  projectId: string; databaseId: string; tableId: string; rowId: string; rows: Row[];
  rowSecurity: boolean; onNavigate: (id: string) => void; onClose: () => void; onDelete: (id: string) => Promise<void>;
}) {
  const [tab, setTab] = useState<"json" | "permissions">("json");
  const [copied, setCopied] = useState(false);
  const update = useUpdateRow(projectId, databaseId, tableId);
  const timerRef = useRef<number | null>(null);

  const index = rows.findIndex((r) => r.$id === rowId);
  const row = index >= 0 ? rows[index] : null;

  useEffect(() => {
    const url = new URL(window.location.href);
    url.searchParams.set("row", rowId);
    window.history.replaceState(null, "", url.toString());
    return () => {
      const cleared = new URL(window.location.href);
      cleared.searchParams.delete("row");
      window.history.replaceState(null, "", cleared.toString());
    };
  }, [rowId]);

  if (!row) return null;

  const json = JSON.stringify(row, null, 2);

  function setPermission(action: "read" | "update" | "delete", role: string, enabled: boolean) {
    const entry = `${action}("${role}")`;
    const current = row!.$permissions;
    const next = enabled ? (current.includes(entry) ? current : [...current, entry]) : current.filter((p) => p !== entry);
    update.mutate({ rowId, permissions: next });
  }

  const roles = [...new Set(row.$permissions.map((p) => /\("(.+)"\)$/.exec(p)?.[1]).filter((r): r is string => !!r))];

  return (
    <Sheet title="Row" size="lg" onClose={onClose}>
      <div className="mb-4 flex items-center justify-between">
        <div className="flex gap-1">
          <button
            type="button"
            className="btn-ghost border border-ink-700 px-2 py-1 text-xs disabled:opacity-30"
            disabled={index <= 0}
            onClick={() => onNavigate(rows[index - 1].$id)}
          >
            ← Prev
          </button>
          <button
            type="button"
            className="btn-ghost border border-ink-700 px-2 py-1 text-xs disabled:opacity-30"
            disabled={index < 0 || index >= rows.length - 1}
            onClick={() => onNavigate(rows[index + 1].$id)}
          >
            Next →
          </button>
        </div>
        <button
          type="button"
          className="btn-ghost border border-ink-700 px-2 py-1 text-xs"
          onClick={() => {
            void navigator.clipboard.writeText(json);
            setCopied(true);
            if (timerRef.current) window.clearTimeout(timerRef.current);
            timerRef.current = window.setTimeout(() => setCopied(false), 1200);
          }}
        >
          {copied ? "✓ Copied" : "Copy as JSON"}
        </button>
      </div>

      <div className="mb-4 flex gap-1 border-b border-ink-800" role="tablist">
        <button type="button" role="tab" aria-selected={tab === "json"} className={`-mb-px border-b-2 px-3 py-2 text-sm font-medium ${tab === "json" ? "border-iris-400 text-ink-100" : "border-transparent text-ink-500"}`} onClick={() => setTab("json")}>
          Raw JSON
        </button>
        <button type="button" role="tab" aria-selected={tab === "permissions"} className={`-mb-px border-b-2 px-3 py-2 text-sm font-medium ${tab === "permissions" ? "border-iris-400 text-ink-100" : "border-transparent text-ink-500"}`} onClick={() => setTab("permissions")}>
          Permissions
        </button>
      </div>

      {tab === "json" ? (
        <pre className="overflow-x-auto rounded-lg border border-ink-800 bg-ink-950 p-3 font-mono text-xs text-ink-300">{json}</pre>
      ) : (
        <div>
          {!rowSecurity ? (
            <p className="mb-3 text-xs text-ink-500">
              row_security is off on this table — table-level permissions govern every row uniformly. Enable it in
              Settings to grant row-specific access.
            </p>
          ) : (
            <>
              <div className="mb-3 overflow-x-auto">
                <table className="w-full text-left text-sm">
                  <thead>
                    <tr className="border-b border-ink-800 text-xs text-ink-500 uppercase">
                      <th className="py-2 pr-4 font-medium">Role</th>
                      <th className="px-2 py-2 text-center font-medium">read</th>
                      <th className="px-2 py-2 text-center font-medium">update</th>
                      <th className="px-2 py-2 text-center font-medium">delete</th>
                    </tr>
                  </thead>
                  <tbody className="divide-y divide-ink-800/60">
                    {roles.length === 0 ? (
                      <tr><td colSpan={4} className="py-4 text-center text-xs text-ink-500">No row-level grants. Table-level permissions still apply.</td></tr>
                    ) : (
                      roles.map((role) => (
                        <tr key={role}>
                          <td className="py-2 pr-4">
                            <RoleLabel projectId={projectId} role={role} />
                          </td>
                          {(["read", "update", "delete"] as const).map((action) => (
                            <td key={action} className="px-2 py-2 text-center">
                              <input
                                type="checkbox"
                                className="accent-iris-500"
                                checked={row.$permissions.includes(`${action}("${role}")`)}
                                onChange={(e) => setPermission(action, role, e.target.checked)}
                              />
                            </td>
                          ))}
                        </tr>
                      ))
                    )}
                  </tbody>
                </table>
              </div>
              <div className="flex justify-end">
                <AddRoleButton
                  projectId={projectId}
                  existingRoles={roles}
                  onPick={(role: string) => setPermission("read", role, true)}
                />
              </div>
            </>
          )}
        </div>
      )}

      <div className="mt-8 border-t border-coral-400/20 pt-5">
        <h3 className="mb-2 text-sm font-medium text-coral-400">Danger zone</h3>
        <ConfirmButton
          label="Delete row"
          title="Delete row?"
          confirmLabel="Delete row"
          successMessage="Row deleted."
          className="btn-ghost border border-ink-700 text-coral-400"
          body={
            <>
              Row <span className="font-mono text-ink-300">{rowId}</span> is removed permanently. Subscribers on
              this table receive a delete event.
            </>
          }
          onConfirm={() => onDelete(rowId)}
        />
      </div>
    </Sheet>
  );
}
