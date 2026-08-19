import { useState, type ReactNode } from "react";
import { ApiError } from "../api/client";
import { useToast } from "./toast";
import { ErrorNote, Modal, Spinner } from "./ui";

/**
 * A destructive action that asks first, reports what happened, and shows the failure if there is
 * one.
 *
 * The console had three different answers to "are you sure?" — type the name (table, function),
 * click the button twice (delete user), and nothing whatsoever (revoke API key, delete
 * webhook/topic/platform/provider, revoke session). The last group also dropped mutation errors on
 * the floor: a rejected revoke rendered identically to a successful one. This is the single
 * pattern for row-level destructive actions; the typed-name danger zones stay as they are, since
 * dropping a whole table deserves the extra friction.
 */
export function ConfirmButton({
  label,
  title,
  body,
  confirmLabel,
  successMessage,
  onConfirm,
  disabled,
  className = "btn-ghost border border-ink-700 px-2 py-1 text-xs text-coral-400",
}: {
  label: ReactNode;
  title: string;
  body: ReactNode;
  confirmLabel: string;
  successMessage: string;
  onConfirm: () => Promise<unknown>;
  disabled?: boolean;
  className?: string;
}) {
  const [open, setOpen] = useState(false);
  const [pending, setPending] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const toast = useToast();

  async function run() {
    setPending(true);
    setError(null);
    try {
      await onConfirm();
      setOpen(false);
      toast.success(successMessage);
    } catch (err) {
      // Keep the dialog open so the message sits next to the button that caused it.
      setError(err instanceof ApiError ? err.message : (err as Error).message);
    } finally {
      setPending(false);
    }
  }

  return (
    <>
      <button type="button" className={className} disabled={disabled} onClick={() => setOpen(true)}>
        {label}
      </button>
      {open ? (
        <Modal title={title} onClose={() => (pending ? undefined : setOpen(false))}>
          <div className="space-y-4">
            {error ? <ErrorNote message={error} /> : null}
            <div className="text-sm text-ink-400">{body}</div>
            <div className="flex justify-end gap-2">
              <button
                type="button"
                className="btn-ghost border border-ink-700"
                disabled={pending}
                onClick={() => setOpen(false)}
              >
                Cancel
              </button>
              <button
                type="button"
                className="btn-ghost border border-coral-400/60 text-coral-400 disabled:opacity-40"
                disabled={pending}
                onClick={() => void run()}
                autoFocus
              >
                {pending ? <Spinner /> : confirmLabel}
              </button>
            </div>
          </div>
        </Modal>
      ) : null}
    </>
  );
}
