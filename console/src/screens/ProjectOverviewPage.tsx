import { Link, useParams } from "@tanstack/react-router";
import { useState } from "react";
import { useProject } from "../api/queries";
import { FullPageSpinner, IdChip, Kbd } from "../components/ui";
import { STR } from "../strings";

export function ProjectOverviewPage() {
  const { projectId } = useParams({ from: "/shell/project/$projectId" });
  const project = useProject(projectId, { pollWhileUnpinged: true });

  if (project.isPending) return <FullPageSpinner />;
  if (project.isError) throw project.error;

  const pinged = Boolean(project.data.lastPingAt);

  return (
    <div className="flex min-h-dvh">
      {/* Project sidebar — Phase 0 has only Overview; later services appear as the
          capabilities endpoint turns their flags on. */}
      <aside className="flex w-52 shrink-0 flex-col border-r border-ink-800 bg-ink-900/50 px-3 py-4">
        <Link to="/" className="btn-ghost mb-4 justify-start text-xs">
          ← {STR.projects}
        </Link>
        <span className="mb-1 truncate px-3 text-sm font-semibold">{project.data.name}</span>
        <span className="mb-6 px-3">
          <IdChip id={project.data.id} />
        </span>
        <nav className="space-y-1">
          <span className="flex items-center justify-between rounded-lg bg-ink-800 px-3 py-2 text-sm font-medium text-ink-100">
            {STR.overview}
            <Kbd>g o</Kbd>
          </span>
        </nav>
      </aside>

      <main className="flex-1 px-8 py-10">
        <h1 className="mb-1 text-2xl font-semibold tracking-tight">{project.data.name}</h1>
        <p className="mb-8 text-sm text-ink-500">
          Created {new Date(project.data.createdAt).toLocaleString()}
        </p>

        {pinged ? <ConnectedCard lastPingAt={project.data.lastPingAt!} /> : <WaitingCard projectId={project.data.id} />}
      </main>
    </div>
  );
}

/** Onboarding: shown until the first real API ping lands, then flips automatically. */
function WaitingCard({ projectId }: { projectId: string }) {
  const snippet = `curl ${window.location.origin}/v1/ping -H "X-Praxy-Project: ${projectId}"`;
  const [copied, setCopied] = useState(false);

  return (
    <div className="surface max-w-2xl p-6">
      <div className="mb-4 flex items-center gap-3">
        <span className="size-2.5 rounded-full bg-amber-400 animate-ping-pulse" />
        <h2 className="text-lg font-medium">Waiting for your first ping…</h2>
      </div>
      <p className="mb-4 text-sm text-ink-400">
        Send any request with your project header and this screen updates the moment it arrives.
      </p>
      <div className="flex items-stretch gap-2">
        <pre className="flex-1 overflow-x-auto rounded-lg border border-ink-700 bg-ink-950 px-4 py-3 font-mono text-xs text-ink-300">
          {snippet}
        </pre>
        <button
          type="button"
          className="btn-ghost border border-ink-700"
          onClick={() => {
            void navigator.clipboard.writeText(snippet);
            setCopied(true);
            setTimeout(() => setCopied(false), 1200);
          }}
        >
          {copied ? "✓" : "Copy"}
        </button>
      </div>
    </div>
  );
}

function ConnectedCard({ lastPingAt }: { lastPingAt: string }) {
  return (
    <div className="surface max-w-2xl p-6">
      <div className="mb-2 flex items-center gap-3">
        <span className="size-2.5 rounded-full bg-mint-400" />
        <h2 className="text-lg font-medium">Connected</h2>
      </div>
      <p className="text-sm text-ink-400">
        Last ping {new Date(lastPingAt).toLocaleString()}. Auth, databases and realtime arrive in
        upcoming phases — this overview grows with them.
      </p>
    </div>
  );
}
