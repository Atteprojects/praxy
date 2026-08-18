import { useEffect, useState, type ReactNode } from "react";

export function Logo({ size = 22 }: { size?: number }) {
  return (
    <span className="inline-flex items-center gap-2 select-none">
      <svg width={size} height={size} viewBox="0 0 24 24" aria-hidden>
        <rect x="2" y="2" width="20" height="20" rx="6" className="fill-iris-500" />
        <path d="M8 17V7.5h4.2a3.3 3.3 0 1 1 0 6.6H10.5" className="stroke-white" strokeWidth="2" fill="none" strokeLinecap="round" />
      </svg>
      <span className="font-semibold tracking-tight text-ink-100">Praxy</span>
    </span>
  );
}

export function Spinner({ className = "" }: { className?: string }) {
  return (
    <svg className={`animate-spin ${className}`} width="18" height="18" viewBox="0 0 24 24" fill="none" aria-label="Loading">
      <circle cx="12" cy="12" r="10" className="stroke-ink-700" strokeWidth="3" />
      <path d="M12 2a10 10 0 0 1 10 10" className="stroke-iris-400" strokeWidth="3" strokeLinecap="round" />
    </svg>
  );
}

export function FullPageSpinner() {
  return (
    <div className="grid min-h-dvh place-items-center">
      <Spinner className="size-6" />
    </div>
  );
}

/** Every entity screen shows its id, always visible, always copyable. */
export function IdChip({ id }: { id: string }) {
  const [copied, setCopied] = useState(false);
  return (
    <button
      type="button"
      onClick={() => {
        void navigator.clipboard.writeText(id);
        setCopied(true);
        setTimeout(() => setCopied(false), 1200);
      }}
      title="Copy ID"
      className="group inline-flex max-w-full items-center gap-1.5 rounded-md border border-ink-700 bg-ink-900 px-2 py-0.5 font-mono text-xs text-ink-300 hover:border-ink-500 hover:text-ink-100 transition-colors cursor-pointer"
    >
      <span className="truncate">{id}</span>
      <span className={copied ? "text-mint-400" : "text-ink-500 group-hover:text-ink-300"}>
        {copied ? "✓" : "⧉"}
      </span>
    </button>
  );
}

export function Field({
  label,
  error,
  children,
}: {
  label: string;
  error?: string;
  children: ReactNode;
}) {
  return (
    <label className="block">
      <span className="mb-1.5 block text-xs font-medium uppercase tracking-wide text-ink-400">{label}</span>
      {children}
      {error ? <span className="mt-1 block text-xs text-coral-400">{error}</span> : null}
    </label>
  );
}

export function ErrorNote({ message }: { message: string }) {
  return (
    <div className="rounded-lg border border-coral-400/30 bg-coral-400/10 px-3 py-2 text-sm text-coral-400">
      {message}
    </div>
  );
}

export function Kbd({ children }: { children: ReactNode }) {
  return (
    <kbd className="rounded border border-ink-700 bg-ink-850 px-1.5 py-0.5 font-mono text-[11px] text-ink-400">
      {children}
    </kbd>
  );
}

const badgeTones = {
  mint: "border-mint-400/30 bg-mint-400/10 text-mint-400",
  amber: "border-amber-400/30 bg-amber-400/10 text-amber-400",
  coral: "border-coral-400/30 bg-coral-400/10 text-coral-400",
  ink: "border-ink-700 bg-ink-850 text-ink-400",
  iris: "border-iris-500/30 bg-iris-500/10 text-iris-300",
} as const;

export function Badge({ tone = "ink", children }: { tone?: keyof typeof badgeTones; children: ReactNode }) {
  return (
    <span className={`inline-flex items-center rounded-md border px-1.5 py-0.5 text-[11px] font-medium ${badgeTones[tone]}`}>
      {children}
    </span>
  );
}

/** Modal shell: backdrop click and Escape close it; content stops propagation. */
export function Modal({ onClose, title, children }: { onClose: () => void; title: string; children: ReactNode }) {
  return (
    <div
      className="fixed inset-0 z-40 grid place-items-center bg-ink-950/70 p-4 backdrop-blur-sm"
      onClick={(e) => e.target === e.currentTarget && onClose()}
      onKeyDown={(e) => e.key === "Escape" && onClose()}
      role="dialog"
      aria-modal
    >
      <div className="surface flex max-h-[85vh] w-full max-w-md flex-col p-6">
        <div className="mb-5 flex items-center justify-between">
          <h2 className="text-lg font-semibold tracking-tight">{title}</h2>
          <button type="button" className="btn-ghost px-2 py-1 text-ink-500" onClick={onClose} aria-label="Close">
            ✕
          </button>
        </div>
        <div className="min-h-0 overflow-y-auto">{children}</div>
      </div>
    </div>
  );
}

/** Side sheet: same backdrop/escape semantics as Modal, anchored to the right edge instead of centered. */
export function Sheet({
  onClose,
  title,
  children,
  footer,
}: {
  onClose: () => void;
  title: string;
  children: ReactNode;
  footer?: ReactNode;
}) {
  return (
    <div
      className="fixed inset-0 z-40 flex justify-end bg-ink-950/70 backdrop-blur-sm"
      onClick={(e) => e.target === e.currentTarget && onClose()}
      onKeyDown={(e) => e.key === "Escape" && onClose()}
      role="dialog"
      aria-modal
    >
      <div className="flex h-full w-full max-w-md flex-col border-l border-ink-800 bg-ink-900 shadow-2xl shadow-black/50">
        <div className="flex items-center justify-between border-b border-ink-800 px-6 py-4">
          <h2 className="text-lg font-semibold tracking-tight">{title}</h2>
          <button type="button" className="btn-ghost px-2 py-1 text-ink-500" onClick={onClose} aria-label="Close">
            ✕
          </button>
        </div>
        <div className="flex-1 overflow-y-auto px-6 py-5">{children}</div>
        {footer ? <div className="border-t border-ink-800 px-6 py-4">{footer}</div> : null}
      </div>
    </div>
  );
}

/** Live "Xs"/"Xm Ys" elapsed readout, ticking once a second while `active`. */
export function useElapsed(since: string | null | undefined, active: boolean): string {
  const [, setTick] = useState(0);
  useEffect(() => {
    if (!active) return;
    const id = setInterval(() => setTick((t) => t + 1), 1000);
    return () => clearInterval(id);
  }, [active]);

  if (!since) return "0s";
  const seconds = Math.max(0, Math.floor((Date.now() - new Date(since).getTime()) / 1000));
  if (seconds < 60) return `${seconds}s`;
  const minutes = Math.floor(seconds / 60);
  return `${minutes}m ${seconds % 60}s`;
}

export function Toggle({
  checked,
  onChange,
  label,
  description,
}: {
  checked: boolean;
  onChange: (value: boolean) => void;
  label: string;
  description?: string;
}) {
  return (
    <label className="flex cursor-pointer items-start justify-between gap-4 py-1">
      <span>
        <span className="block text-sm font-medium text-ink-100">{label}</span>
        {description ? <span className="mt-0.5 block text-xs text-ink-500">{description}</span> : null}
      </span>
      <button
        type="button"
        role="switch"
        aria-checked={checked}
        onClick={() => onChange(!checked)}
        className={`relative mt-0.5 h-5 w-9 shrink-0 rounded-full transition-colors ${checked ? "bg-iris-500" : "bg-ink-700"}`}
      >
        <span
          className={`absolute top-0.5 size-4 rounded-full bg-white transition-transform ${checked ? "translate-x-4.5" : "translate-x-0.5"}`}
        />
      </button>
    </label>
  );
}

export function Tabs<T extends string>({
  tabs,
  active,
  onSelect,
}: {
  tabs: readonly { id: T; label: string }[];
  active: T;
  onSelect: (tab: T) => void;
}) {
  return (
    <div className="mb-6 flex gap-1 border-b border-ink-800" role="tablist">
      {tabs.map((tab) => (
        <button
          key={tab.id}
          type="button"
          role="tab"
          aria-selected={tab.id === active}
          onClick={() => onSelect(tab.id)}
          className={`-mb-px border-b-2 px-3 py-2 text-sm font-medium transition-colors ${
            tab.id === active
              ? "border-iris-400 text-ink-100"
              : "border-transparent text-ink-500 hover:text-ink-300"
          }`}
        >
          {tab.label}
        </button>
      ))}
    </div>
  );
}

/** Data-table shell shared by every Phase 1 list screen. Wide content scrolls in-place. */
export function DataTable({ headers, children }: { headers: string[]; children: ReactNode }) {
  return (
    <div className="surface overflow-x-auto">
      <table className="w-full text-left text-sm">
        <thead>
          <tr className="border-b border-ink-800">
            {headers.map((header, i) => (
              <th key={i} className="px-4 py-3 text-xs font-medium uppercase tracking-wide text-ink-500">
                {header}
              </th>
            ))}
          </tr>
        </thead>
        <tbody className="divide-y divide-ink-800/60">{children}</tbody>
      </table>
    </div>
  );
}

/** Ghost empty state with the real headers — the table teaches its own shape. */
export function EmptyState({
  headers,
  title,
  action,
}: {
  headers: string[];
  title: string;
  action?: ReactNode;
}) {
  return (
    <div className="surface relative overflow-hidden">
      <table className="w-full text-left text-sm" aria-hidden>
        <thead>
          <tr className="border-b border-ink-800">
            {headers.map((header, i) => (
              <th key={i} className="px-4 py-3 text-xs font-medium uppercase tracking-wide text-ink-500">
                {header}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {[0, 1, 2].map((row) => (
            <tr key={row} className="border-b border-ink-800/40">
              {headers.map((_, i) => (
                <td key={i} className="px-4 py-3.5">
                  <div className="h-3 w-2/3 rounded bg-ink-850" />
                </td>
              ))}
            </tr>
          ))}
        </tbody>
      </table>
      <div className="absolute inset-0 top-11 grid place-items-center bg-ink-950/40">
        <div className="text-center">
          <p className="mb-3 text-sm text-ink-400">{title}</p>
          {action}
        </div>
      </div>
    </div>
  );
}

export function timeAgo(iso: string | null | undefined): string {
  if (!iso) return "—";
  const seconds = Math.floor((Date.now() - new Date(iso).getTime()) / 1000);
  if (seconds < 60) return "just now";
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m ago`;
  if (seconds < 86400) return `${Math.floor(seconds / 3600)}h ago`;
  if (seconds < 30 * 86400) return `${Math.floor(seconds / 86400)}d ago`;
  return new Date(iso).toLocaleDateString();
}
