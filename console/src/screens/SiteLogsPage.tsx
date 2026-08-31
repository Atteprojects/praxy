import { useParams } from "@tanstack/react-router";
import { useMemo } from "react";
import { useSite, useSiteRequests } from "../api/sites";
import type { DataGridColumn } from "../components/DataGrid";
import { DataGrid } from "../components/DataGrid";
import { Badge, EmptyState, FullPageSpinner, timeAgo } from "../components/ui";
import type { SiteRequestLog } from "../api/types";
import { SiteDetailHeader } from "./SiteDetailHeader";

const HEADERS = ["Method", "Path", "Status", "Duration", "Created"];

/**
 * Metadata-only, no drill-down — matches Appwrite's own Sites "Logs" tab, the bar
 * docs/handoff/sites-request-logs-prompt.md's non-goals set ("a plain table... not more"). Rows come
 * from SiteRequestLogWorker's async drain of SiteProxyMiddleware's channel, so a request made just now
 * may take a moment to appear here — this polls rather than expecting the write to be immediate.
 */
export function SiteLogsPage() {
  const { projectId, siteId } = useParams({ strict: false }) as { projectId: string; siteId: string };
  const site = useSite(projectId, siteId);
  const requests = useSiteRequests(projectId, siteId);

  const columns = useMemo<DataGridColumn<SiteRequestLog>[]>(() => [
    {
      id: "method",
      header: "Method",
      cell: ({ row }) => <span className="font-mono text-xs text-ink-300">{row.original.method}</span>,
    },
    {
      id: "path",
      header: "Path",
      cell: ({ row }) => <span className="truncate font-mono text-xs text-ink-400">{row.original.path}</span>,
    },
    {
      id: "status",
      header: "Status",
      cell: ({ row }) => <StatusBadge statusCode={row.original.statusCode} />,
    },
    {
      id: "duration",
      header: "Duration",
      cell: ({ row }) => <span className="text-xs text-ink-400">{row.original.durationMs}ms</span>,
    },
    {
      id: "created",
      header: "Created",
      cell: ({ row }) => <span className="text-xs text-ink-400">{timeAgo(row.original.createdAt)}</span>,
    },
  ], []);

  if (site.isPending || requests.isPending) return <FullPageSpinner />;
  if (site.isError) throw site.error;
  if (requests.isError) throw requests.error;

  return (
    <div>
      <SiteDetailHeader projectId={projectId} site={site.data} active="logs" />

      {requests.data.total === 0 ? (
        <EmptyState
          headers={HEADERS}
          title="No requests logged yet — visit the site's public URL to see activity here."
        />
      ) : (
        <DataGrid columns={columns} data={requests.data.requests} getRowId={(row) => row.id} maxHeight="65vh" />
      )}
    </div>
  );
}

function StatusBadge({ statusCode }: { statusCode: number }) {
  if (statusCode >= 500) return <Badge tone="coral">{statusCode}</Badge>;
  if (statusCode >= 400) return <Badge tone="amber">{statusCode}</Badge>;
  return <Badge tone="mint">{statusCode}</Badge>;
}
