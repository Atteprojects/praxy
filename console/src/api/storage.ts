import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api, ApiError } from "./client";
import type {
  Bucket, BucketList, BucketPermissions, ErrorEnvelope, StorageUsage, StoredFile, StoredFileList,
} from "./types";

const base = (projectId: string) => `/console/projects/${projectId}/storage`;

// ---- usage ----

export function useStorageUsage(projectId: string) {
  return useQuery({
    queryKey: ["projects", projectId, "storage", "usage"],
    queryFn: () => api<StorageUsage>(`${base(projectId)}/usage`),
  });
}

// ---- buckets ----

export function useBuckets(projectId: string) {
  return useQuery({
    queryKey: ["projects", projectId, "buckets"],
    queryFn: () => api<BucketList>(`${base(projectId)}/buckets`),
  });
}

export function useBucket(projectId: string, bucketId: string) {
  return useQuery({
    queryKey: ["projects", projectId, "buckets", bucketId],
    queryFn: () => api<Bucket>(`${base(projectId)}/buckets/${bucketId}`),
  });
}

export function useCreateBucket(projectId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: { key: string; name: string; maxFileSizeBytes?: number; allowedMimeTypes?: string[] }) =>
      api<Bucket>(`${base(projectId)}/buckets`, { method: "POST", body: input }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["projects", projectId, "buckets"] }),
  });
}

export function useUpdateBucket(projectId: string, bucketId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: {
      name?: string;
      enabled?: boolean;
      maxFileSizeBytes?: number;
      /** `[]` clears the allow-list back to "any type"; omit to leave it unchanged. */
      allowedMimeTypes?: string[];
    }) => api<Bucket>(`${base(projectId)}/buckets/${bucketId}`, { method: "PATCH", body: input }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ["projects", projectId, "buckets"] });
      void queryClient.invalidateQueries({ queryKey: ["projects", projectId, "buckets", bucketId] });
    },
  });
}

/** Always destructive — every file in the bucket goes with it, so `force` is not optional. */
export function useDeleteBucket(projectId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (bucketId: string) =>
      api<void>(`${base(projectId)}/buckets/${bucketId}?force=true`, { method: "DELETE" }),
    onSuccess: (_result, bucketId) =>
      queryClient.invalidateQueries({
        queryKey: ["projects", projectId, "buckets"],
        predicate: (query) => query.queryKey[3] !== bucketId,
      }),
  });
}

// ---- permissions ----

export function useBucketPermissions(projectId: string, bucketId: string) {
  return useQuery({
    queryKey: ["projects", projectId, "buckets", bucketId, "permissions"],
    queryFn: () => api<BucketPermissions>(`${base(projectId)}/buckets/${bucketId}/permissions`),
  });
}

export function useUpdateBucketPermissions(projectId: string, bucketId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (permissions: string[]) =>
      api<BucketPermissions>(`${base(projectId)}/buckets/${bucketId}/permissions`, {
        method: "PATCH",
        body: { permissions },
      }),
    onSuccess: () =>
      queryClient.invalidateQueries({ queryKey: ["projects", projectId, "buckets", bucketId, "permissions"] }),
  });
}

// ---- files ----

export function useFiles(projectId: string, bucketId: string) {
  return useQuery({
    queryKey: ["projects", projectId, "buckets", bucketId, "files"],
    queryFn: () => api<StoredFileList>(`${base(projectId)}/buckets/${bucketId}/files?limit=100`),
  });
}

export function useDeleteFile(projectId: string, bucketId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (fileId: string) =>
      api<void>(`${base(projectId)}/buckets/${bucketId}/files/${fileId}`, { method: "DELETE" }),
    onSuccess: () => invalidateAfterFileChange(queryClient, projectId, bucketId),
  });
}

/**
 * Uploads through `XMLHttpRequest` rather than `fetch`, for one reason: real progress. `fetch` has
 * no upload-progress event at all (a request `ReadableStream` needs HTTP/2 plus `duplex: "half"`,
 * and is still unsupported in Safari), and a multi-megabyte upload with no feedback reads as a
 * hung console.
 */
export function uploadFile(
  projectId: string,
  bucketId: string,
  file: File,
  onProgress?: (fraction: number) => void,
): Promise<StoredFile> {
  return new Promise((resolve, reject) => {
    const request = new XMLHttpRequest();
    request.open(
      "POST",
      `/v1${base(projectId)}/buckets/${bucketId}/files?name=${encodeURIComponent(file.name)}`,
    );
    request.withCredentials = true;
    // The body *is* the bytes, and this header becomes the stored file's mime type.
    request.setRequestHeader("Content-Type", file.type || "application/octet-stream");

    request.upload.addEventListener("progress", (event) => {
      if (event.lengthComputable) onProgress?.(event.loaded / event.total);
    });

    request.addEventListener("load", () => {
      if (request.status >= 200 && request.status < 300) {
        resolve(JSON.parse(request.responseText) as StoredFile);
        return;
      }
      reject(new ApiError(parseEnvelope(request)));
    });
    request.addEventListener("error", () =>
      reject(new ApiError(fallbackEnvelope("The upload failed to reach the server.", 0))));
    request.addEventListener("abort", () =>
      reject(new ApiError(fallbackEnvelope("The upload was cancelled.", 0))));

    request.send(file);
  });
}

export function useUploadFile(projectId: string, bucketId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ file, onProgress }: { file: File; onProgress?: (fraction: number) => void }) =>
      uploadFile(projectId, bucketId, file, onProgress),
    onSuccess: () => invalidateAfterFileChange(queryClient, projectId, bucketId),
  });
}

/**
 * Fetches the bytes with the session cookie attached and hands the browser a blob URL to save. A
 * plain `<a href>` to the download route would work in the console's own origin, but this keeps the
 * file's stored name on the saved file without the server needing a `Content-Disposition` header —
 * which is deliberately Storage Phase 2's problem, not Phase 1's.
 */
export async function downloadFile(
  projectId: string, bucketId: string, file: { id: string; name: string },
): Promise<void> {
  const response = await fetch(
    `/v1${base(projectId)}/buckets/${bucketId}/files/${file.id}/download`,
    { credentials: "include" },
  );
  if (!response.ok) {
    let envelope: ErrorEnvelope;
    try {
      envelope = (await response.json()) as ErrorEnvelope;
    } catch {
      envelope = fallbackEnvelope(`Download failed with status ${response.status}`, response.status);
    }
    throw new ApiError(envelope);
  }

  const url = URL.createObjectURL(await response.blob());
  try {
    const anchor = document.createElement("a");
    anchor.href = url;
    anchor.download = file.name;
    anchor.click();
  } finally {
    // Revoked on the next tick: revoking synchronously can beat the browser's own read of the URL.
    setTimeout(() => URL.revokeObjectURL(url), 0);
  }
}

function invalidateAfterFileChange(
  queryClient: ReturnType<typeof useQueryClient>, projectId: string, bucketId: string,
) {
  void queryClient.invalidateQueries({ queryKey: ["projects", projectId, "buckets", bucketId, "files"] });
  void queryClient.invalidateQueries({ queryKey: ["projects", projectId, "storage", "usage"] });
}

function parseEnvelope(request: XMLHttpRequest): ErrorEnvelope {
  try {
    return JSON.parse(request.responseText) as ErrorEnvelope;
  } catch {
    return fallbackEnvelope(`Upload failed with status ${request.status}`, request.status);
  }
}

function fallbackEnvelope(message: string, code: number): ErrorEnvelope {
  return { message, code, type: "general_server_error", version: "", requestId: "" };
}
