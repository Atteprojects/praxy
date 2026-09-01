import { useEffect, useMemo, useRef, useState } from "react";
import { createPortal } from "react-dom";
import { useQuery } from "@tanstack/react-query";
import { api } from "../api/client";
import type { Row, RowList } from "../api/types";
import { usePanelPosition, PickerRow } from "./RolePicker";
import { Spinner } from "./ui";

const PANEL_WIDTH = 320;

/**
 * Fetches one page of the target table's rows for the picker to search over. No "which field
 * represents a row to a human" concept exists in Praxy today (docs/research/table-relationships.md
 * explicitly defers that), so this is a first cut: fetch a page, filter by `$id` prefix
 * client-side, rather than a server-side prefix query — the query DSL's `startsWith` is explicitly
 * rejected for `$id` today (QueryCompiler.CompileStringOrArrayOp), and loosening that is a separate,
 * broader change this phase doesn't need.
 */
function useRelationshipCandidates(projectId: string, databaseId: string, tableId: string) {
  return useQuery({
    queryKey: ["projects", projectId, "databases", databaseId, "tables", tableId, "rows", "relationship-picker"],
    queryFn: () => {
      const params = new URLSearchParams();
      params.append("queries[]", JSON.stringify({ method: "limit", values: [100] }));
      params.append("total", "false");
      return api<RowList>(`/console/projects/${projectId}/databases/${databaseId}/tables/${tableId}/rows?${params.toString()}`);
    },
  });
}

/**
 * The relationship column's row-search popover (docs/research/table-relationships.md Phase 3),
 * modeled on RolePicker.tsx's portal-popover-with-search structure — positioning, open/close,
 * Escape/click-outside — not its role-specific (fixed local list) content.
 */
export function RelationshipPicker({
  projectId,
  databaseId,
  targetTableId,
  anchorRef,
  excludeIds = [],
  onPick,
  onClose,
}: {
  projectId: string;
  databaseId: string;
  targetTableId: string;
  anchorRef: React.RefObject<HTMLElement | null>;
  /** Already-linked ids (array column) — greyed out so the same row can't be linked twice. */
  excludeIds?: string[];
  onPick: (rowId: string) => void;
  onClose: () => void;
}) {
  const [search, setSearch] = useState("");
  const panelRef = useRef<HTMLDivElement>(null);
  const position = usePanelPosition(anchorRef);
  const candidates = useRelationshipCandidates(projectId, databaseId, targetTableId);

  useEffect(() => {
    function onPointerDown(event: MouseEvent) {
      const target = event.target as Node;
      if (panelRef.current?.contains(target) || anchorRef.current?.contains(target)) return;
      onClose();
    }
    function onKeyDown(event: KeyboardEvent) {
      if (event.key !== "Escape") return;
      event.stopPropagation();
      onClose();
    }
    document.addEventListener("mousedown", onPointerDown);
    document.addEventListener("keydown", onKeyDown, true);
    return () => {
      document.removeEventListener("mousedown", onPointerDown);
      document.removeEventListener("keydown", onKeyDown, true);
    };
  }, [anchorRef, onClose]);

  const filtered = useMemo(() => {
    const rows = candidates.data?.rows ?? [];
    const term = search.trim();
    return term ? rows.filter((r) => r.$id.startsWith(term)) : rows;
  }, [candidates.data, search]);

  function pick(row: Row) {
    onPick(row.$id);
    onClose();
  }

  return createPortal(
    <div
      ref={panelRef}
      style={{ position: "fixed", top: position?.top ?? -9999, left: position?.left ?? -9999, width: PANEL_WIDTH }}
      className="z-[70] flex flex-col overflow-hidden rounded-xl border border-ink-700 bg-ink-900 shadow-xl shadow-black/50"
      // A portal's click still bubbles through the *React* tree of whoever rendered it (this picker
      // is rendered from a grid cell), not the DOM tree — without this, picking a row here also
      // triggered the underlying DataGrid row's onClick and opened its row sheet.
      onClick={(e) => e.stopPropagation()}
    >
      <div className="flex items-center justify-between border-b border-ink-800 px-3 py-2">
        <span className="text-xs font-medium text-ink-300">Pick a row</span>
        <button type="button" className="text-ink-500 hover:text-ink-300 cursor-pointer" onClick={onClose} aria-label="Close">
          ✕
        </button>
      </div>

      <div className="min-h-0 flex-1 overflow-y-auto p-2" style={{ maxHeight: position?.maxHeight }}>
        <input
          className="input-base mb-2 font-mono text-xs"
          autoFocus
          placeholder="Search by row id…"
          value={search}
          onChange={(e) => setSearch(e.target.value)}
        />
        {candidates.isPending ? <Spinner className="mx-auto my-3 size-4" /> : null}
        {candidates.isError ? <p className="px-2.5 py-3 text-xs text-coral-400">Couldn't load rows.</p> : null}
        {!candidates.isPending && !candidates.isError && filtered.length === 0 ? (
          <p className="px-2.5 py-3 text-xs text-ink-500">No rows match.</p>
        ) : null}
        {filtered.map((row) => (
          <PickerRow key={row.$id} disabled={excludeIds.includes(row.$id)} onClick={() => pick(row)} hint={row.$id}>
            {row.$id.slice(0, 12)}…
          </PickerRow>
        ))}
      </div>
    </div>,
    document.body,
  );
}

function extractRowId(value: unknown): string {
  if (value && typeof value === "object" && "$id" in value) return String((value as Row).$id);
  return String(value);
}

/**
 * Chips for each currently-linked row plus a trigger that opens the picker above — shared shape
 * between RowsPage.tsx's `EditableCell` (grid) and `CreateRowSheet` (create form). `null` for
 * `targetTableId` means the column's target table was itself force-deleted (Phase 2 orphaning) —
 * there's nothing left to search against, so the caller falls back to a plain text input instead of
 * rendering this component at all.
 */
export function RelationshipValueEditor({
  ids, array, targetTableId, projectId, databaseId, onChange,
}: {
  ids: unknown[];
  array: boolean;
  targetTableId: string;
  projectId: string;
  databaseId: string;
  onChange: (ids: string[]) => void;
}) {
  const [pickerOpen, setPickerOpen] = useState(false);
  const anchorRef = useRef<HTMLButtonElement>(null);
  const resolvedIds = ids.map(extractRowId);

  return (
    <div className="flex flex-wrap items-center gap-1.5">
      {resolvedIds.map((id) => (
        <span key={id} className="inline-flex items-center gap-1 rounded-md border border-ink-700 bg-ink-850 px-1.5 py-0.5 font-mono text-[11px] text-ink-300">
          {id.slice(0, 8)}…
          <button
            type="button"
            className="text-ink-500 hover:text-coral-400"
            onClick={(e) => { e.stopPropagation(); onChange(resolvedIds.filter((i) => i !== id)); }}
          >
            ✕
          </button>
        </span>
      ))}
      {array || resolvedIds.length === 0 ? (
        <button
          ref={anchorRef}
          type="button"
          className="btn-ghost border border-ink-700 px-2 py-0.5 text-xs"
          onClick={(e) => { e.stopPropagation(); setPickerOpen(true); }}
        >
          + pick row
        </button>
      ) : null}
      {pickerOpen ? (
        <RelationshipPicker
          projectId={projectId}
          databaseId={databaseId}
          targetTableId={targetTableId}
          anchorRef={anchorRef}
          excludeIds={resolvedIds}
          onPick={(id) => onChange(array ? [...resolvedIds, id] : [id])}
          onClose={() => setPickerOpen(false)}
        />
      ) : null}
    </div>
  );
}
