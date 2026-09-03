import { Link } from "@tanstack/react-router";
import type { Bucket } from "../api/types";
import { Badge, IdChip } from "../components/ui";
import { formatBytes } from "./storageFormat";

/** Shared by Files/Settings: name + chips + the tab row between the two bucket sub-views — same shape as SiteDetailHeader. */
export function BucketDetailHeader({
  projectId,
  bucket,
  active,
}: {
  projectId: string;
  bucket: Bucket;
  active: "files" | "settings";
}) {
  return (
    <div className="mb-6">
      <div className="mb-2 flex flex-wrap items-center gap-3">
        <h1 className="text-2xl font-semibold tracking-tight">{bucket.name}</h1>
        <IdChip id={bucket.id} />
        {bucket.enabled ? null : <Badge tone="ink">disabled</Badge>}
      </div>
      <p className="mb-4 font-mono text-sm text-ink-500">
        {bucket.key} · max {formatBytes(bucket.maxFileSizeBytes)} per file ·{" "}
        {bucket.allowedMimeTypes === null ? "any type" : bucket.allowedMimeTypes.join(", ")}
      </p>
      <div className="flex gap-1 border-b border-ink-800" role="tablist">
        <TabLink to="files" label="Files" active={active === "files"} projectId={projectId} bucketId={bucket.id} />
        <TabLink to="settings" label="Settings" active={active === "settings"} projectId={projectId} bucketId={bucket.id} />
      </div>
    </div>
  );
}

const TAB_ROUTES = {
  files: "/project/$projectId/storage/$bucketId",
  settings: "/project/$projectId/storage/$bucketId/settings",
} as const;

function TabLink({
  to,
  label,
  active,
  projectId,
  bucketId,
}: {
  to: keyof typeof TAB_ROUTES;
  label: string;
  active: boolean;
  projectId: string;
  bucketId: string;
}) {
  return (
    <Link
      to={TAB_ROUTES[to]}
      params={{ projectId, bucketId }}
      activeOptions={{ exact: to === "files" }}
      className={`-mb-px border-b-2 px-3 py-2 text-sm font-medium transition-colors ${
        active ? "border-iris-400 text-ink-100" : "border-transparent text-ink-500 hover:text-ink-300"
      }`}
      role="tab"
      aria-selected={active}
    >
      {label}
    </Link>
  );
}
