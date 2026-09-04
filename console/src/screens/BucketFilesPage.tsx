import { useParams } from "@tanstack/react-router";
import { useRef, useState, type DragEvent } from "react";
import { ApiError } from "../api/client";
import {
  useBucket, useDeleteFile, useFiles, useUpdateFilePermissions, downloadFile, uploadFile,
} from "../api/storage";
import type { Bucket, StoredFile } from "../api/types";
import { ConfirmButton } from "../components/ConfirmButton";
import { AddRoleButton, RoleLabel } from "../components/RolePicker";
import { useToast } from "../components/toast";
import { DataTable, ErrorNote, FullPageSpinner, IdChip, Sheet, Spinner, timeAgo } from "../components/ui";
import { BucketDetailHeader } from "./BucketDetailHeader";
import { formatBytes } from "./storageFormat";

const HEADERS = ["Name", "Type", "Size", "Uploaded", ""];

/** A file can be granted read/update/delete — never create, which has no file to attach to yet. */
const FILE_ACTIONS = ["read", "update", "delete"] as const;

export function BucketFilesPage() {
  const { projectId, bucketId } = useParams({ strict: false }) as { projectId: string; bucketId: string };
  const bucket = useBucket(projectId, bucketId);
  const files = useFiles(projectId, bucketId);
  const [sheetFileId, setSheetFileId] = useState<string | null>(null);

  if (bucket.isPending || files.isPending) return <FullPageSpinner />;
  if (bucket.isError) throw bucket.error;
  if (files.isError) throw files.error;

  const sheetFile = files.data.files.find((f) => f.id === sheetFileId);

  return (
    <div>
      <BucketDetailHeader projectId={projectId} bucket={bucket.data} active="files" />
      <UploadPanel projectId={projectId} bucketId={bucketId} />

      {files.data.total === 0 ? (
        <p className="surface px-4 py-10 text-center text-sm text-ink-500">
          No files yet. Drop one above to upload it.
        </p>
      ) : (
        <DataTable headers={HEADERS}>
          {files.data.files.map((file) => (
            <FileRow
              key={file.id}
              projectId={projectId}
              bucketId={bucketId}
              bucket={bucket.data}
              file={file}
              onOpenPermissions={() => setSheetFileId(file.id)}
            />
          ))}
        </DataTable>
      )}

      {sheetFile ? (
        <FilePermissionsSheet
          projectId={projectId}
          bucketId={bucketId}
          bucket={bucket.data}
          file={sheetFile}
          onClose={() => setSheetFileId(null)}
        />
      ) : null}
    </div>
  );
}

function FileRow({ projectId, bucketId, bucket, file, onOpenPermissions }: {
  projectId: string; bucketId: string; bucket: Bucket; file: StoredFile; onOpenPermissions: () => void;
}) {
  const remove = useDeleteFile(projectId, bucketId);
  const [downloading, setDownloading] = useState(false);
  const toast = useToast();

  async function onDownload() {
    setDownloading(true);
    try {
      await downloadFile(projectId, bucketId, file);
    } catch (error) {
      toast.error(error instanceof ApiError ? error.message : (error as Error).message);
    } finally {
      setDownloading(false);
    }
  }

  return (
    <tr>
      <td className="max-w-72 px-4 py-3">
        <p className="truncate font-medium text-ink-100" title={file.name}>{file.name}</p>
        <div className="mt-1 flex items-center gap-2">
          <IdChip id={file.id} />
          <span className="font-mono text-[11px] text-ink-600" title="SHA-256, computed while the upload streamed">
            {file.checksum.slice(0, 12)}…
          </span>
          {bucket.fileSecurity ? (
            <span
              className={`text-[11px] ${file.$permissions.length === 0 ? "text-amber-400" : "text-ink-600"}`}
              title={file.$permissions.join(" · ") || "Only the bucket matrix can reach this file"}
            >
              {file.$permissions.length === 0
                ? "no file grants"
                : `${file.$permissions.length} file grant${file.$permissions.length === 1 ? "" : "s"}`}
            </span>
          ) : null}
        </div>
      </td>
      <td className="px-4 py-3 font-mono text-xs text-ink-400">{file.mimeType}</td>
      <td className="px-4 py-3 whitespace-nowrap text-ink-400">
        {formatBytes(file.sizeBytes)}
        <span className="ml-1.5 text-[11px] text-ink-600" title={`${file.chunkCount} × ${formatBytes(file.chunkSizeBytes)} chunks`}>
          {file.chunkCount === 1 ? "1 chunk" : `${file.chunkCount} chunks`}
        </span>
      </td>
      <td className="px-4 py-3 whitespace-nowrap text-ink-400">{timeAgo(file.createdAt)}</td>
      <td className="px-4 py-3 text-right whitespace-nowrap">
        <button
          type="button"
          className="btn-ghost border border-ink-700 px-2 py-1 text-xs"
          disabled={downloading}
          onClick={() => void onDownload()}
        >
          {downloading ? <Spinner /> : "Download"}
        </button>{" "}
        <button
          type="button"
          className="btn-ghost border border-ink-700 px-2 py-1 text-xs"
          onClick={onOpenPermissions}
        >
          Permissions
        </button>{" "}
        <ConfirmButton
          label="Delete"
          title="Delete file?"
          confirmLabel="Delete file"
          successMessage={`Deleted "${file.name}".`}
          body={
            <>
              <span className="font-mono text-ink-300">{file.name}</span> and its stored bytes are removed
              immediately. This cannot be undone.
            </>
          }
          onConfirm={() => remove.mutateAsync(file.id)}
        />
      </td>
    </tr>
  );
}

/**
 * Per-file grants, in the same matrix shape the row sheet uses — same components, same grammar,
 * one action column short because a file cannot grant its own creation.
 *
 * These grants are **additive**: they widen access, never narrow it. A bucket-level
 * <code>read</code> already reaches every file here, which is why the note below points at the
 * bucket matrix rather than pretending this sheet is the whole answer.
 */
function FilePermissionsSheet({ projectId, bucketId, bucket, file, onClose }: {
  projectId: string; bucketId: string; bucket: Bucket; file: StoredFile; onClose: () => void;
}) {
  const update = useUpdateFilePermissions(projectId, bucketId, file.id);
  const error = update.error instanceof ApiError ? update.error : null;
  const roles = [
    ...new Set(file.$permissions.map((p) => /\("(.+)"\)$/.exec(p)?.[1]).filter((r): r is string => !!r)),
  ];

  function setPermission(action: (typeof FILE_ACTIONS)[number], role: string, enabled: boolean) {
    const entry = `${action}("${role}")`;
    const next = enabled
      ? (file.$permissions.includes(entry) ? file.$permissions : [...file.$permissions, entry])
      : file.$permissions.filter((p) => p !== entry);
    update.mutate(next);
  }

  return (
    <Sheet title={file.name} onClose={onClose}>
      {!bucket.fileSecurity ? (
        <p className="text-xs text-ink-500">
          File security is off on this bucket — the bucket permission matrix governs every file
          uniformly. Turn it on in Settings to grant access to individual files.
        </p>
      ) : (
        <>
          <p className="mb-3 text-xs text-ink-500">
            Granted <span className="text-ink-300">in addition to</span> the bucket matrix, never
            instead of it: a role the bucket already grants reaches this file whatever is ticked here.
          </p>
          {error ? <div className="mb-3"><ErrorNote message={error.message} /></div> : null}

          <div className="mb-3 overflow-x-auto">
            <table className="w-full text-left text-sm">
              <thead>
                <tr className="border-b border-ink-800 text-xs text-ink-500 uppercase">
                  <th className="py-2 pr-4 font-medium">Role</th>
                  {FILE_ACTIONS.map((action) => (
                    <th key={action} className="px-2 py-2 text-center font-medium">{action}</th>
                  ))}
                </tr>
              </thead>
              <tbody className="divide-y divide-ink-800/60">
                {roles.length === 0 ? (
                  <tr>
                    <td colSpan={4} className="py-4 text-center text-xs text-ink-500">
                      No grants on this file. Only the bucket matrix can reach it.
                    </td>
                  </tr>
                ) : (
                  roles.map((role) => (
                    <tr key={role}>
                      <td className="py-2 pr-4">
                        <RoleLabel projectId={projectId} role={role} />
                      </td>
                      {FILE_ACTIONS.map((action) => (
                        <td key={action} className="px-2 py-2 text-center">
                          <input
                            type="checkbox"
                            className="accent-iris-500"
                            checked={file.$permissions.includes(`${action}("${role}")`)}
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

          <div className="flex items-center justify-end gap-2">
            {update.isPending ? <Spinner className="size-3" /> : null}
            <AddRoleButton
              projectId={projectId}
              existingRoles={roles}
              onPick={(role: string) => setPermission("read", role, true)}
            />
          </div>
        </>
      )}
    </Sheet>
  );
}

/**
 * Drop zone plus a real progress bar. Progress is worth the `XMLHttpRequest` in `api/storage.ts`
 * (`fetch` reports none): the server streams the body into chunk rows as it arrives, so a
 * multi-megabyte upload genuinely takes time and silence would read as a hang.
 */
function UploadPanel({ projectId, bucketId }: { projectId: string; bucketId: string }) {
  const [dragging, setDragging] = useState(false);
  const [progress, setProgress] = useState<{ name: string; fraction: number } | null>(null);
  const [error, setError] = useState<string | null>(null);
  const input = useRef<HTMLInputElement>(null);
  const toast = useToast();
  const files = useFiles(projectId, bucketId);

  async function upload(list: FileList | null) {
    if (!list || list.length === 0) return;
    setError(null);
    for (const file of Array.from(list)) {
      setProgress({ name: file.name, fraction: 0 });
      try {
        await uploadFile(projectId, bucketId, file, (fraction) => setProgress({ name: file.name, fraction }));
        toast.success(`Uploaded "${file.name}".`);
      } catch (err) {
        setError(err instanceof ApiError ? err.message : (err as Error).message);
        break;
      } finally {
        setProgress(null);
      }
    }
    await files.refetch();
    if (input.current) input.current.value = "";
  }

  function onDrop(e: DragEvent<HTMLDivElement>) {
    e.preventDefault();
    setDragging(false);
    void upload(e.dataTransfer.files);
  }

  return (
    <div className="mb-4">
      <div
        onDragOver={(e) => {
          e.preventDefault();
          setDragging(true);
        }}
        onDragLeave={() => setDragging(false)}
        onDrop={onDrop}
        className={`surface flex flex-col items-center gap-2 px-6 py-8 text-center transition-colors ${
          dragging ? "border-iris-500 bg-iris-500/5" : ""
        }`}
      >
        {progress ? (
          <div className="w-full max-w-sm">
            <p className="mb-2 truncate text-sm text-ink-300">
              Uploading <span className="font-mono">{progress.name}</span> — {Math.round(progress.fraction * 100)}%
            </p>
            <div className="h-1.5 overflow-hidden rounded-full bg-ink-850">
              <div
                className="h-full rounded-full bg-iris-500 transition-[width] duration-150"
                style={{ width: `${Math.max(progress.fraction * 100, 2)}%` }}
              />
            </div>
          </div>
        ) : (
          <>
            <p className="text-sm text-ink-400">Drop files here to upload</p>
            <button type="button" className="btn-ghost border border-ink-700 text-xs" onClick={() => input.current?.click()}>
              Choose a file
            </button>
            <input
              ref={input}
              type="file"
              multiple
              className="hidden"
              onChange={(e) => void upload(e.target.files)}
            />
          </>
        )}
      </div>
      {error ? <div className="mt-3"><ErrorNote message={error} /></div> : null}
    </div>
  );
}
