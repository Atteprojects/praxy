import { Link, useNavigate, useParams } from "@tanstack/react-router";
import { useState, type FormEvent } from "react";
import { ApiError } from "../api/client";
import { useBuckets, useCreateBucket, useStorageUsage } from "../api/storage";
import {
  Badge, DataTable, EmptyState, ErrorNote, Field, FullPageSpinner, IdChip, Modal, PageHeader, Spinner, timeAgo,
} from "../components/ui";
import { formatBytes } from "./storageFormat";

const HEADERS = ["Bucket", "Max file size", "Accepts", "Status", "Created"];

export function StoragePage() {
  const { projectId } = useParams({ strict: false }) as { projectId: string };
  const buckets = useBuckets(projectId);
  const usage = useStorageUsage(projectId);
  const [creating, setCreating] = useState(false);

  if (buckets.isPending) return <FullPageSpinner />;
  if (buckets.isError) throw buckets.error;

  return (
    <div>
      <PageHeader
        title="Storage"
        description="File buckets. A bucket is the permission boundary — like a table, it denies everyone until you grant a role."
        actions={
          <button type="button" className="btn-primary" onClick={() => setCreating(true)}>
            + Create bucket
          </button>
        }
      />

      {usage.data ? <UsageBar usedBytes={usage.data.usedBytes} maxBytes={usage.data.maxBytes} /> : null}

      {creating ? <CreateBucketModal projectId={projectId} onClose={() => setCreating(false)} /> : null}

      {buckets.data.total === 0 ? (
        <EmptyState
          headers={HEADERS}
          title="No buckets yet. Create one to start storing files."
          action={
            <button type="button" className="btn-primary" onClick={() => setCreating(true)}>
              + Create bucket
            </button>
          }
        />
      ) : (
        <DataTable headers={HEADERS}>
          {buckets.data.buckets.map((bucket) => (
            <tr key={bucket.id}>
              <td className="px-4 py-3">
                <Link
                  to="/project/$projectId/storage/$bucketId"
                  params={{ projectId, bucketId: bucket.id }}
                  className="font-medium text-ink-100 hover:text-iris-300"
                >
                  {bucket.name}
                </Link>
                <div className="mt-1 flex items-center gap-2">
                  <span className="font-mono text-xs text-ink-500">{bucket.key}</span>
                  <IdChip id={bucket.id} />
                </div>
              </td>
              <td className="px-4 py-3 whitespace-nowrap text-ink-400">{formatBytes(bucket.maxFileSizeBytes)}</td>
              <td className="px-4 py-3 text-xs text-ink-400">
                {bucket.allowedMimeTypes === null ? (
                  <span className="text-ink-500">any type</span>
                ) : (
                  <span className="font-mono">{bucket.allowedMimeTypes.join(", ")}</span>
                )}
              </td>
              <td className="px-4 py-3">
                {bucket.enabled ? <Badge tone="mint">enabled</Badge> : <Badge tone="ink">disabled</Badge>}
              </td>
              <td className="px-4 py-3 whitespace-nowrap text-ink-400">{timeAgo(bucket.createdAt)}</td>
            </tr>
          ))}
        </DataTable>
      )}
    </div>
  );
}

/**
 * Project storage against its quota. Worth a permanent place rather than an error-time surprise:
 * stored bytes live in Postgres, so they land in every `backup.sh` dump — this number is what an
 * operator's backups grow by (docs/self-host.md).
 */
function UsageBar({ usedBytes, maxBytes }: { usedBytes: number; maxBytes: number }) {
  const fraction = maxBytes > 0 ? Math.min(1, usedBytes / maxBytes) : 0;
  const tone = fraction > 0.9 ? "bg-coral-400" : fraction > 0.7 ? "bg-amber-400" : "bg-iris-500";

  return (
    <div className="surface mb-4 px-4 py-3">
      <div className="mb-2 flex items-baseline justify-between gap-3 text-xs">
        <span className="text-ink-400">
          <span className="font-medium text-ink-100">{formatBytes(usedBytes)}</span> of {formatBytes(maxBytes)} stored
        </span>
        <span className="text-ink-500">Stored files are included in every backup.</span>
      </div>
      <div className="h-1.5 overflow-hidden rounded-full bg-ink-850">
        <div className={`h-full rounded-full ${tone}`} style={{ width: `${Math.max(fraction * 100, 1)}%` }} />
      </div>
    </div>
  );
}

function CreateBucketModal({ projectId, onClose }: { projectId: string; onClose: () => void }) {
  const create = useCreateBucket(projectId);
  const usage = useStorageUsage(projectId);
  const navigate = useNavigate();
  const [name, setName] = useState("");
  const [key, setKey] = useState("");
  const [keyTouched, setKeyTouched] = useState(false);
  const [maxFileSizeMb, setMaxFileSizeMb] = useState("");
  const [mimeTypes, setMimeTypes] = useState("");
  const error = create.error instanceof ApiError ? create.error : null;

  /** Bucket keys follow the tables engine's `Keys` grammar: letter first, then letters/digits/underscore. */
  function slugify(value: string) {
    const slug = value.toLowerCase().replace(/[^a-z0-9_]/g, "_").replace(/_+/g, "_").slice(0, 64);
    return /^[a-z]/.test(slug) ? slug : `b_${slug}`.slice(0, 64);
  }

  async function onSubmit(e: FormEvent) {
    e.preventDefault();
    const parsedMb = Number.parseFloat(maxFileSizeMb);
    const bucket = await create.mutateAsync({
      key: key || slugify(name),
      name,
      maxFileSizeBytes: Number.isFinite(parsedMb) && parsedMb > 0
        ? Math.round(parsedMb * 1024 * 1024)
        : undefined,
      allowedMimeTypes: mimeTypes
        .split(",")
        .map((t) => t.trim())
        .filter(Boolean),
    });
    onClose();
    void navigate({
      to: "/project/$projectId/storage/$bucketId/settings",
      params: { projectId, bucketId: bucket.id },
    });
  }

  return (
    <Modal title="Create bucket" onClose={onClose}>
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
            placeholder="Avatars"
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
            placeholder="avatars"
          />
        </Field>
        <Field
          label="Max file size (MB, optional)"
          error={error?.fieldErrors("maxFileSizeBytes")[0]}
        >
          <input
            className="input-base"
            type="number"
            min="0"
            step="any"
            value={maxFileSizeMb}
            onChange={(e) => setMaxFileSizeMb(e.target.value)}
            placeholder={usage.data ? String(Math.round(usage.data.maxFileSizeBytes / 1024 / 1024)) : "50"}
          />
          <span className="mt-1 block text-[11px] text-ink-500">
            Capped at this instance&rsquo;s per-file limit
            {usage.data ? ` (${formatBytes(usage.data.maxFileSizeBytes)})` : ""} — a bucket can narrow it, never widen it.
          </span>
        </Field>
        <Field label="Allowed types (optional)" error={error?.fieldErrors("allowedMimeTypes")[0]}>
          <input
            className="input-base font-mono text-xs"
            value={mimeTypes}
            onChange={(e) => setMimeTypes(e.target.value)}
            placeholder="image/*, application/pdf"
          />
          <span className="mt-1 block text-[11px] text-ink-500">
            Comma-separated mime types or <span className="font-mono">type/*</span> wildcards. Leave blank to accept any type.
          </span>
        </Field>
        <div className="rounded-lg border border-ink-800 bg-ink-950 px-3 py-2 text-[11px] text-ink-500">
          A new bucket denies everyone. You&rsquo;ll land on its permissions next.
        </div>
        <button type="submit" className="btn-primary w-full" disabled={create.isPending}>
          {create.isPending ? <Spinner /> : "Create bucket"}
        </button>
      </form>
    </Modal>
  );
}
