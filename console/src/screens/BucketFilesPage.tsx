import { useParams } from "@tanstack/react-router";
import { useRef, useState, type DragEvent } from "react";
import { ApiError } from "../api/client";
import { useBucket, useDeleteFile, useFiles, downloadFile, uploadFile } from "../api/storage";
import type { StoredFile } from "../api/types";
import { ConfirmButton } from "../components/ConfirmButton";
import { useToast } from "../components/toast";
import { DataTable, ErrorNote, FullPageSpinner, IdChip, Spinner, timeAgo } from "../components/ui";
import { BucketDetailHeader } from "./BucketDetailHeader";
import { formatBytes } from "./storageFormat";

const HEADERS = ["Name", "Type", "Size", "Uploaded", ""];

export function BucketFilesPage() {
  const { projectId, bucketId } = useParams({ strict: false }) as { projectId: string; bucketId: string };
  const bucket = useBucket(projectId, bucketId);
  const files = useFiles(projectId, bucketId);

  if (bucket.isPending || files.isPending) return <FullPageSpinner />;
  if (bucket.isError) throw bucket.error;
  if (files.isError) throw files.error;

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
            <FileRow key={file.id} projectId={projectId} bucketId={bucketId} file={file} />
          ))}
        </DataTable>
      )}
    </div>
  );
}

function FileRow({ projectId, bucketId, file }: { projectId: string; bucketId: string; file: StoredFile }) {
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
