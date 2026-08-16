import { Badge, Spinner, useElapsed } from "./ui";

/**
 * The async-job UX roadmap.md names as the phase's "beat Appwrite" feature: elapsed time while
 * processing, a captured error on failure, cancel and retry. Shared by the Indexes screen (which
 * has a real job behind every processing/failed row) and the Columns screen (status-only today —
 * no column operation goes through the job queue yet, so it never renders interactive).
 */
export function JobStatusBadge({
  status,
  error,
  startedAt,
  onCancel,
  onRetry,
  busy,
}: {
  status: "available" | "processing" | "failed";
  error?: string | null;
  startedAt?: string;
  onCancel?: () => void;
  onRetry?: () => void;
  busy?: boolean;
}) {
  const elapsed = useElapsed(startedAt, status === "processing");

  if (status === "available") {
    return <Badge tone="mint">available</Badge>;
  }

  if (status === "processing") {
    return (
      <span className="inline-flex items-center gap-2">
        <Badge tone="amber">
          <Spinner className="mr-1 inline size-3 align-[-2px]" /> processing · {elapsed}
        </Badge>
        {onCancel ? (
          <button
            type="button"
            className="text-xs text-ink-500 underline decoration-dotted hover:text-coral-400 disabled:opacity-50"
            onClick={onCancel}
            disabled={busy}
          >
            Cancel
          </button>
        ) : null}
      </span>
    );
  }

  return (
    <span className="inline-flex items-center gap-2">
      <span title={error ?? undefined}>
        <Badge tone="coral">failed</Badge>
      </span>
      {error ? <span className="max-w-40 truncate text-xs text-coral-400">{error}</span> : null}
      {onRetry ? (
        <button
          type="button"
          className="text-xs text-ink-500 underline decoration-dotted hover:text-iris-300 disabled:opacity-50"
          onClick={onRetry}
          disabled={busy}
        >
          Retry
        </button>
      ) : null}
    </span>
  );
}
