import { Link, useParams } from "@tanstack/react-router";
import { useState } from "react";
import { useConnectionCount, useProject } from "../api/queries";
import { FullPageSpinner } from "../components/ui";

export function ProjectOverviewPage() {
  const { projectId } = useParams({ strict: false }) as { projectId: string };
  const project = useProject(projectId, { pollWhileUnpinged: true });

  if (project.isPending) return <FullPageSpinner />;
  if (project.isError) throw project.error;

  const pinged = Boolean(project.data.lastPingAt);

  return (
    <div>
      <h1 className="mb-1 text-2xl font-semibold tracking-tight">{project.data.name}</h1>
      <p className="mb-8 text-sm text-ink-500">
        Created {new Date(project.data.createdAt).toLocaleString()}
      </p>

      {pinged ? <ConnectedCard lastPingAt={project.data.lastPingAt!} /> : <WaitingCard projectId={project.data.id} />}
      <ConnectionsTile projectId={project.data.id} />
    </div>
  );
}

/** The realtime inspector's cheapest possible advertisement: a live count, updating on its own. */
function ConnectionsTile({ projectId }: { projectId: string }) {
  const connections = useConnectionCount(projectId);
  return (
    <Link
      to="/project/$projectId/realtime"
      params={{ projectId }}
      className="surface mt-4 flex max-w-2xl items-center justify-between p-6 transition-colors hover:border-ink-600"
    >
      <div>
        <h2 className="text-lg font-medium">Realtime</h2>
        <p className="mt-0.5 text-sm text-ink-400">Live WebSocket connections on this project.</p>
      </div>
      <span className="text-3xl font-semibold tabular-nums text-ink-100">
        {connections.data?.count ?? "—"}
      </span>
    </Link>
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
        Last ping {new Date(lastPingAt).toLocaleString()}. Head to Auth to create your first
        users and teams, or Databases to model your data — messaging, functions and webhooks
        arrive in upcoming phases.
      </p>
    </div>
  );
}
