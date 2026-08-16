import { flexRender, type RowData } from "@tanstack/react-table";
import { getCoreRowModel, useLegacyTable, type LegacyColumnDef } from "@tanstack/react-table/legacy";
import { useVirtualizer } from "@tanstack/react-virtual";
import { useRef, type ReactNode } from "react";

/** Convenience alias so screens don't need to reach into the legacy entrypoint themselves. */
export type DataGridColumn<T extends RowData> = LegacyColumnDef<T, unknown>;

/**
 * The spreadsheet primitive console-design.md calls for: "one primitive, three screens" (columns,
 * indexes, and Phase 3's rows). Built on TanStack Table + Virtual so it scales from a handful of
 * columns today to thousands of rows later without a rewrite.
 *
 * Built against `@tanstack/react-table`'s `legacy` compatibility entrypoint — v9's native `useTable`
 * is a from-scratch reactive-store API with no v8 precedent to lean on; the legacy shim is
 * TanStack's own supported migration path and gives every capability this grid actually needs
 * (core row model, typed columns, `flexRender`) with a well-understood surface. Worth revisiting
 * once there's room to adopt the native API deliberately.
 */
export function DataGrid<T extends RowData>({
  columns,
  data,
  getRowId,
  onRowClick,
  emptyState,
  estimateRowHeight = 44,
  maxHeight = "70vh",
}: {
  columns: DataGridColumn<T>[];
  data: T[];
  getRowId?: (row: T) => string;
  onRowClick?: (row: T) => void;
  emptyState?: ReactNode;
  estimateRowHeight?: number;
  maxHeight?: string;
}) {
  const table = useLegacyTable({
    data,
    columns,
    getCoreRowModel: getCoreRowModel(),
    getRowId: getRowId ? (row) => getRowId(row) : undefined,
  });

  const rows = table.getRowModel().rows;
  const scrollRef = useRef<HTMLDivElement>(null);
  const virtualizer = useVirtualizer({
    count: rows.length,
    getScrollElement: () => scrollRef.current,
    estimateSize: () => estimateRowHeight,
    overscan: 8,
  });

  if (rows.length === 0 && emptyState) {
    return <>{emptyState}</>;
  }

  const virtualRows = virtualizer.getVirtualItems();
  const totalSize = virtualizer.getTotalSize();
  const paddingTop = virtualRows.length > 0 ? virtualRows[0].start : 0;
  const paddingBottom = virtualRows.length > 0 ? totalSize - virtualRows[virtualRows.length - 1].end : 0;

  return (
    <div className="surface overflow-hidden">
      <div ref={scrollRef} className="overflow-auto" style={{ maxHeight }}>
        <table className="w-full border-collapse text-left text-sm">
          <thead className="sticky top-0 z-10 bg-ink-900">
            {table.getHeaderGroups().map((headerGroup) => (
              <tr key={headerGroup.id} className="border-b border-ink-800">
                {headerGroup.headers.map((header) => (
                  <th
                    key={header.id}
                    className="px-4 py-3 text-xs font-medium tracking-wide text-ink-500 uppercase"
                  >
                    {header.isPlaceholder ? null : flexRender(header.column.columnDef.header, header.getContext())}
                  </th>
                ))}
              </tr>
            ))}
          </thead>
          <tbody className="divide-y divide-ink-800/60">
            {paddingTop > 0 ? (
              <tr aria-hidden style={{ height: paddingTop }}>
                <td colSpan={columns.length} />
              </tr>
            ) : null}
            {virtualRows.map((virtualRow) => {
              const row = rows[virtualRow.index];
              return (
                <tr
                  key={row.id}
                  className={onRowClick ? "cursor-pointer transition-colors hover:bg-ink-850/60" : undefined}
                  onClick={() => onRowClick?.(row.original)}
                >
                  {row.getVisibleCells().map((cell) => (
                    <td key={cell.id} className="px-4 py-3">
                      {flexRender(cell.column.columnDef.cell, cell.getContext())}
                    </td>
                  ))}
                </tr>
              );
            })}
            {paddingBottom > 0 ? (
              <tr aria-hidden style={{ height: paddingBottom }}>
                <td colSpan={columns.length} />
              </tr>
            ) : null}
          </tbody>
        </table>
      </div>
    </div>
  );
}
