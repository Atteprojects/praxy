import { useEffect, useRef, useState, type ReactNode } from "react";

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

const FOCUSABLE =
  'a[href],button:not([disabled]),input:not([disabled]),select:not([disabled]),textarea:not([disabled]),[tabindex]:not([tabindex="-1"])';

/**
 * Only the topmost open dialog reacts to Escape, so closing a modal opened from inside a sheet
 * doesn't tear down both at once.
 */
const dialogStack: symbol[] = [];

/**
 * Scroll-lock state is shared, not per-dialog. Each dialog used to save and restore
 * `document.body.style` itself, which broke as soon as two were open: closing a sheet and the
 * confirm modal inside it in the same tick could restore them out of order, and the body was left
 * permanently locked. Reference-counting means the lock is applied once on the way in and released
 * once the last dialog is gone, whatever order they unmount in.
 */
let scrollLockCount = 0;
let savedBodyStyle: { overflow: string; paddingRight: string } | null = null;

function acquireScrollLock() {
  if (scrollLockCount++ > 0) return;
  savedBodyStyle = {
    overflow: document.body.style.overflow,
    paddingRight: document.body.style.paddingRight,
  };
  // Compensate for the vanishing scrollbar so the page behind doesn't jump sideways.
  const scrollbarWidth = window.innerWidth - document.documentElement.clientWidth;
  document.body.style.overflow = "hidden";
  if (scrollbarWidth > 0) document.body.style.paddingRight = `${scrollbarWidth}px`;
}

function releaseScrollLock() {
  if (--scrollLockCount > 0) return;
  scrollLockCount = 0;
  if (!savedBodyStyle) return;
  document.body.style.overflow = savedBodyStyle.overflow;
  document.body.style.paddingRight = savedBodyStyle.paddingRight;
  savedBodyStyle = null;
}

/**
 * Shared chrome for `Modal` and `Sheet`: reliable Escape, a focus trap, focus restore on close,
 * and a background scroll lock.
 *
 * Escape used to be an `onKeyDown` on the backdrop div. React keydown only fires for events that
 * bubble out of the focused node, and the div isn't focusable — so Escape worked when a field
 * inside happened to hold focus and did nothing otherwise (the mobile nav sheet, which autofocuses
 * nothing, could never be closed with the keyboard). Listening on `document` fixes that for real.
 */
function useDialogChrome(onClose: () => void) {
  const ref = useRef<HTMLDivElement>(null);
  const onCloseRef = useRef(onClose);
  onCloseRef.current = onClose;

  useEffect(() => {
    const token = Symbol("dialog");
    dialogStack.push(token);
    const previouslyFocused = document.activeElement as HTMLElement | null;
    acquireScrollLock();

    function onKeyDown(event: KeyboardEvent) {
      if (dialogStack[dialogStack.length - 1] !== token) return;

      if (event.key === "Escape") {
        event.stopPropagation();
        onCloseRef.current();
        return;
      }
      if (event.key !== "Tab" || !ref.current) return;

      const focusable = [...ref.current.querySelectorAll<HTMLElement>(FOCUSABLE)].filter(
        (element) => element.offsetParent !== null,
      );
      if (focusable.length === 0) return;
      const first = focusable[0];
      const last = focusable[focusable.length - 1];
      const active = document.activeElement;

      if (!event.shiftKey && (active === last || !ref.current.contains(active))) {
        event.preventDefault();
        first.focus();
      } else if (event.shiftKey && (active === first || !ref.current.contains(active))) {
        event.preventDefault();
        last.focus();
      }
    }

    document.addEventListener("keydown", onKeyDown, true);

    // Let any `autoFocus` inside run first; only take focus ourselves if nothing claimed it.
    const focusFrame = requestAnimationFrame(() => {
      if (ref.current && !ref.current.contains(document.activeElement)) ref.current.focus();
    });

    return () => {
      cancelAnimationFrame(focusFrame);
      document.removeEventListener("keydown", onKeyDown, true);
      dialogStack.splice(dialogStack.indexOf(token), 1);
      releaseScrollLock();
      previouslyFocused?.focus?.();
    };
  }, []);

  return ref;
}

let dialogTitleSeq = 0;

/** Modal shell: backdrop click and Escape close it; content stops propagation. */
export function Modal({ onClose, title, children }: { onClose: () => void; title: string; children: ReactNode }) {
  const ref = useDialogChrome(onClose);
  const [titleId] = useState(() => `dialog-title-${++dialogTitleSeq}`);

  return (
    <div
      className="animate-backdrop-in fixed inset-0 z-40 grid place-items-center bg-ink-950/70 p-4 backdrop-blur-sm"
      onClick={(e) => e.target === e.currentTarget && onClose()}
    >
      <div
        ref={ref}
        tabIndex={-1}
        role="dialog"
        aria-modal
        aria-labelledby={titleId}
        className="surface animate-modal-in flex max-h-[85vh] w-full max-w-md flex-col p-5 outline-none sm:p-6"
      >
        <div className="mb-5 flex items-center justify-between">
          <h2 id={titleId} className="text-lg font-semibold tracking-tight">
            {title}
          </h2>
          <button type="button" className="btn-ghost p-2 text-ink-500" onClick={onClose} aria-label="Close">
            ✕
          </button>
        </div>
        <div className="min-h-0 overflow-y-auto">{children}</div>
      </div>
    </div>
  );
}

/** Side sheet: same backdrop/escape semantics as Modal, anchored to an edge instead of centered. */
export function Sheet({
  onClose,
  title,
  children,
  footer,
  side = "right",
  size = "md",
}: {
  onClose: () => void;
  title: string;
  children: ReactNode;
  footer?: ReactNode;
  side?: "left" | "right";
  /** `lg` for sheets carrying a table or a JSON dump; `md` suits a plain form or a nav list. */
  size?: "md" | "lg";
}) {
  const ref = useDialogChrome(onClose);
  const [titleId] = useState(() => `dialog-title-${++dialogTitleSeq}`);

  return (
    <div
      className={`animate-backdrop-in fixed inset-0 z-40 flex bg-ink-950/70 backdrop-blur-sm ${
        side === "left" ? "justify-start" : "justify-end"
      }`}
      onClick={(e) => e.target === e.currentTarget && onClose()}
    >
      <div
        ref={ref}
        tabIndex={-1}
        role="dialog"
        aria-modal
        aria-labelledby={titleId}
        className={`flex h-full w-full flex-col bg-ink-900 shadow-2xl shadow-black/50 outline-none ${
          size === "lg" ? "max-w-xl" : "max-w-md"
        } ${
          side === "left"
            ? "animate-sheet-in-left border-r border-ink-800"
            : "animate-sheet-in-right border-l border-ink-800"
        }`}
      >
        <div className="flex items-center justify-between border-b border-ink-800 px-6 py-4">
          <h2 id={titleId} className="text-lg font-semibold tracking-tight">
            {title}
          </h2>
          <button type="button" className="btn-ghost p-2 text-ink-500" onClick={onClose} aria-label="Close">
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
      <table className="w-full text-left text-sm [&_tbody_tr]:transition-colors [&_tbody_tr]:hover:bg-ink-850/40">
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

/**
 * The one page-header shape every screen uses: title (with optional trailing chips), an optional
 * description, an optional control cluster on the right, and an optional tab row underneath.
 *
 * Screens used to hand-roll this, and drifted: list screens put their primary action inline with
 * the title, while tabbed screens (Messaging) pushed it onto a separate right-aligned row below
 * the tabs, so the "create" button moved between screens.
 */
export function PageHeader({
  title,
  chips,
  description,
  actions,
  tabs,
}: {
  title: ReactNode;
  chips?: ReactNode;
  description?: ReactNode;
  actions?: ReactNode;
  tabs?: ReactNode;
}) {
  return (
    <div className="mb-6">
      <div className="flex flex-wrap items-start justify-between gap-x-4 gap-y-3">
        <div className="min-w-0">
          <div className="flex flex-wrap items-center gap-3">
            <h1 className="text-2xl font-semibold tracking-tight">{title}</h1>
            {chips}
          </div>
          {description ? <p className="mt-1.5 max-w-prose text-sm text-ink-500">{description}</p> : null}
        </div>
        {actions ? <div className="flex shrink-0 flex-wrap items-center gap-2">{actions}</div> : null}
      </div>
      {tabs ? <div className="mt-5">{tabs}</div> : null}
    </div>
  );
}
