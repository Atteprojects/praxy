import { useState, type ReactNode } from "react";

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
