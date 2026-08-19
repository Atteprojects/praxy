import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from "react";

export type ToastTone = "success" | "error" | "info";

export type Toast = {
  id: number;
  tone: ToastTone;
  message: string;
  /** Optional follow-up, e.g. "View indexes" on a job-started toast (console-design.md). */
  action?: { label: string; onClick: () => void };
};

type ToastInput = Omit<Toast, "id">;

const ToastContext = createContext<((toast: ToastInput) => void) | null>(null);

/**
 * The console's only feedback channel for anything that happens outside a modal — deletes,
 * toggles, permission edits, retries. Before this, those mutations were silent on success and,
 * worse, silent on failure: a rejected revoke looked exactly like a successful one.
 */
export function ToastProvider({ children }: { children: ReactNode }) {
  const [toasts, setToasts] = useState<Toast[]>([]);
  const nextId = useRef(1);

  const push = useCallback((toast: ToastInput) => {
    const id = nextId.current++;
    setToasts((current) => [...current, { ...toast, id }]);
  }, []);

  const dismiss = useCallback((id: number) => {
    setToasts((current) => current.filter((toast) => toast.id !== id));
  }, []);

  return (
    <ToastContext.Provider value={push}>
      {children}
      <div
        className="pointer-events-none fixed inset-x-0 bottom-0 z-[60] flex flex-col items-center gap-2 p-4 sm:items-end"
        role="region"
        aria-label="Notifications"
      >
        {toasts.map((toast) => (
          <ToastRow key={toast.id} toast={toast} onDismiss={() => dismiss(toast.id)} />
        ))}
      </div>
    </ToastContext.Provider>
  );
}

const TONE_STYLES: Record<ToastTone, { border: string; dot: string }> = {
  success: { border: "border-mint-400/30", dot: "bg-mint-400" },
  error: { border: "border-coral-400/40", dot: "bg-coral-400" },
  info: { border: "border-ink-700", dot: "bg-iris-400" },
};

/** Errors stay until dismissed — they usually carry something the user needs to read. */
const TIMEOUT_MS: Record<ToastTone, number | null> = {
  success: 4000,
  info: 5000,
  error: null,
};

function ToastRow({ toast, onDismiss }: { toast: Toast; onDismiss: () => void }) {
  useEffect(() => {
    const timeout = TIMEOUT_MS[toast.tone];
    if (timeout === null) return;
    const id = window.setTimeout(onDismiss, timeout);
    return () => window.clearTimeout(id);
  }, [toast.tone, onDismiss]);

  const tone = TONE_STYLES[toast.tone];
  return (
    <div
      role={toast.tone === "error" ? "alert" : "status"}
      className={`animate-toast-in pointer-events-auto flex w-full max-w-sm items-start gap-3 rounded-xl border ${tone.border} bg-ink-850 px-4 py-3 shadow-lg shadow-black/40`}
    >
      <span className={`mt-1.5 size-2 shrink-0 rounded-full ${tone.dot}`} />
      <span className="min-w-0 flex-1 text-sm text-ink-100">{toast.message}</span>
      {toast.action ? (
        <button
          type="button"
          className="shrink-0 text-xs font-medium text-iris-300 hover:text-iris-400 cursor-pointer"
          onClick={() => {
            toast.action?.onClick();
            onDismiss();
          }}
        >
          {toast.action.label}
        </button>
      ) : null}
      <button
        type="button"
        className="shrink-0 text-ink-500 hover:text-ink-300 cursor-pointer"
        onClick={onDismiss}
        aria-label="Dismiss"
      >
        ✕
      </button>
    </div>
  );
}

export function useToast() {
  const push = useContext(ToastContext);
  if (!push) throw new Error("useToast must be used inside <ToastProvider>");
  return useMemo(
    () => ({
      success: (message: string, action?: Toast["action"]) => push({ tone: "success", message, action }),
      error: (message: string, action?: Toast["action"]) => push({ tone: "error", message, action }),
      info: (message: string, action?: Toast["action"]) => push({ tone: "info", message, action }),
    }),
    [push],
  );
}
