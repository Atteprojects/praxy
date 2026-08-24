import { useNavigate, useParams } from "@tanstack/react-router";
import { useMemo, useRef, useState, type ReactNode } from "react";
import {
  useActivateSiteDeployment, useCreateSiteDeployment, useDeploySiteStarterTemplate, useSite,
  useSiteDeployment, useSiteDeployments,
} from "../api/sites";
import { ApiError } from "../api/client";
import type { DataGridColumn } from "../components/DataGrid";
import { DataGrid } from "../components/DataGrid";
import {
  Badge, EmptyState, ErrorNote, FullPageSpinner, Sheet, Spinner, formatElapsed, timeAgo, useElapsed,
} from "../components/ui";
import type { SiteDeployment, SiteDeploymentStatus } from "../api/types";
import { SiteDetailHeader } from "./SiteDetailHeader";

const HEADERS = ["Created", "Status", "Size", "Image", "", ""];

export function SiteDeploymentsPage() {
  const { projectId, siteId } = useParams({ strict: false }) as { projectId: string; siteId: string };
  const site = useSite(projectId, siteId);
  const deployments = useSiteDeployments(projectId, siteId);
  const create = useCreateSiteDeployment(projectId, siteId);
  const deployTemplate = useDeploySiteStarterTemplate(projectId, siteId);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const fileInput = useRef<HTMLInputElement>(null);
  const uploadButton = useRef<HTMLButtonElement>(null);
  const uploadError = create.error instanceof ApiError ? create.error : null;
  const templateError = deployTemplate.error instanceof ApiError ? deployTemplate.error : null;

  const activeDeploymentId = site.data?.activeDeploymentId ?? null;

  const columns = useMemo<DataGridColumn<SiteDeployment>[]>(() => [
    {
      id: "created",
      header: "Created",
      cell: ({ row }) => <span className="text-xs text-ink-400">{timeAgo(row.original.createdAt)}</span>,
    },
    {
      id: "status",
      header: "Status",
      cell: ({ row }) => <DeploymentStatusBadge status={row.original.status} />,
    },
    {
      id: "size",
      header: "Size",
      cell: ({ row }) => (
        <span className="text-xs text-ink-400">{Math.ceil(row.original.sourceSizeBytes / 1024)} KB</span>
      ),
    },
    {
      id: "image",
      header: "Image",
      cell: ({ row }) => (
        <span className="font-mono text-xs text-ink-400">{row.original.imageTag ?? "—"}</span>
      ),
    },
    {
      id: "active",
      header: "",
      // The site's currently active deployment, not merely "has been activated at some point" —
      // activatedAt stays set on a deployment forever once it's first activated, even after a
      // redeploy supersedes it, so activatedAt alone can't tell "active" from "was active once."
      cell: ({ row }) => (row.original.id === activeDeploymentId ? <Badge tone="mint">active</Badge> : null),
    },
    {
      id: "preview",
      header: "",
      // Every ready deployment gets its own reachable preview URL (Sites Phase 2), active or not —
      // stop the row-click (which opens the deployment sheet) from also firing on this link.
      cell: ({ row }) =>
        row.original.previewUrl ? (
          <a
            href={row.original.previewUrl}
            target="_blank"
            rel="noreferrer"
            onClick={(e) => e.stopPropagation()}
            className="text-xs text-iris-300 hover:text-iris-200 hover:underline"
          >
            Preview ↗
          </a>
        ) : null,
    },
  ], [activeDeploymentId]);

  if (site.isPending || deployments.isPending) return <FullPageSpinner />;
  if (site.isError) throw site.error;
  if (deployments.isError) throw deployments.error;

  async function onFileChosen(file: File | undefined) {
    if (!file) return;
    const created = await create.mutateAsync(file);
    setSelectedId(created.id);
    if (fileInput.current) fileInput.current.value = "";
  }

  async function onDeployTemplate() {
    const created = await deployTemplate.mutateAsync();
    setSelectedId(created.id);
  }

  return (
    <div>
      <SiteDetailHeader projectId={projectId} site={site.data} active="deployments" />

      <div className="mb-4 flex items-center justify-between">
        <p className="max-w-xl text-xs text-ink-500">
          Upload a <code className="text-ink-300">.tar</code> of your Next.js app's source (
          <code className="text-ink-300">next.config.js</code> must set{" "}
          <code className="text-ink-300">output: "standalone"</code>), or deploy the starter
          template to see a working site without one. A successful build activates automatically —
          starting a real container — and older ready builds can be re-activated below to roll back.
        </p>
        <div className="flex shrink-0 items-center gap-2">
          <button
            type="button"
            className="btn-ghost border border-ink-700"
            disabled={deployTemplate.isPending}
            onClick={() => void onDeployTemplate()}
          >
            {deployTemplate.isPending ? <Spinner /> : "Deploy starter template"}
          </button>
          <input
            ref={fileInput}
            type="file"
            accept=".tar,.tar.gz,.tgz"
            className="hidden"
            onChange={(e) => void onFileChosen(e.target.files?.[0])}
          />
          <button
            ref={uploadButton}
            type="button"
            className="btn-primary"
            disabled={create.isPending}
            onClick={() => fileInput.current?.click()}
          >
            {create.isPending ? <Spinner /> : "Upload deployment"}
          </button>
        </div>
      </div>
      {uploadError ? <div className="mb-4"><ErrorNote message={uploadError.message} /></div> : null}
      {templateError ? <div className="mb-4"><ErrorNote message={templateError.message} /></div> : null}

      {deployments.data.total === 0 ? (
        <EmptyState
          headers={HEADERS}
          title="No deployments yet. Upload your own tar, or deploy the starter template to see a real site live in seconds."
          action={
            <button
              type="button"
              className="btn-primary"
              disabled={deployTemplate.isPending}
              onClick={() => void onDeployTemplate()}
            >
              {deployTemplate.isPending ? <Spinner /> : "Deploy starter template"}
            </button>
          }
        />
      ) : (
        <DataGrid
          columns={columns}
          data={deployments.data.deployments}
          getRowId={(row) => row.id}
          onRowClick={(row) => setSelectedId(row.id)}
          maxHeight="65vh"
        />
      )}

      {selectedId ? (
        <DeploymentSheet
          key={selectedId}
          projectId={projectId}
          siteId={siteId}
          deploymentId={selectedId}
          activeDeploymentId={activeDeploymentId}
          siteIsRunning={site.data.isRunning}
          sitePublicUrl={site.data.publicUrl}
          onClose={() => setSelectedId(null)}
          onDeployAgain={() => {
            setSelectedId(null);
            uploadButton.current?.focus();
          }}
        />
      ) : null}
    </div>
  );
}

function DeploymentStatusBadge({ status }: { status: SiteDeploymentStatus }) {
  if (status === "ready") return <Badge tone="mint">ready</Badge>;
  if (status === "failed") return <Badge tone="coral">failed</Badge>;
  if (status === "building") return <Badge tone="amber">building</Badge>;
  return <Badge tone="ink">queued</Badge>;
}

function DeploymentSheet({
  projectId,
  siteId,
  deploymentId,
  activeDeploymentId,
  siteIsRunning,
  sitePublicUrl,
  onClose,
  onDeployAgain,
}: {
  projectId: string;
  siteId: string;
  deploymentId: string;
  activeDeploymentId: string | null;
  siteIsRunning: boolean;
  sitePublicUrl: string;
  onClose: () => void;
  onDeployAgain: () => void;
}) {
  const detail = useSiteDeployment(projectId, siteId, deploymentId);
  const activate = useActivateSiteDeployment(projectId, siteId);
  const navigate = useNavigate();
  const [logSearch, setLogSearch] = useState("");
  const [copied, setCopied] = useState(false);
  const [view, setView] = useState<"auto" | "log">("auto");

  const isLiveBuild = detail.data?.status === "building" || detail.data?.status === "queued";
  // Hooks must run every render, so this reads straight off `detail.data` rather than the
  // post-guard `d` below (which only exists once the query has settled).
  const liveElapsed = useElapsed(detail.data?.createdAt, isLiveBuild);

  if (detail.isPending) {
    return (
      <Sheet onClose={onClose} title="Deployment">
        <div className="grid place-items-center py-10"><Spinner /></div>
      </Sheet>
    );
  }
  if (detail.isError) throw detail.error;

  const d = detail.data;
  const isActive = d.id === activeDeploymentId;
  // "Ready but not the site's current deployment" is what makes a deployment activatable — a
  // superseded deployment keeps its own activatedAt timestamp forever, so that alone can't
  // distinguish "currently active" from "was active once" (this is exactly what makes rollback via
  // this button possible after a redeploy).
  const canActivate = d.status === "ready" && !isActive;
  // "ready" only means "buildable" — the site's container has to actually be the one running for
  // this specific deployment before it's truly live (see docs/handoff/sites-phase-1-report.md's
  // Known gaps).
  const isTrulyLive = d.status === "ready" && isActive && siteIsRunning;
  const showSuccess = isTrulyLive && view === "auto";
  const elapsedLabel = isLiveBuild
    ? liveElapsed
    : formatElapsed((new Date(d.updatedAt).getTime() - new Date(d.createdAt).getTime()) / 1000);

  function onCopyLog() {
    void navigator.clipboard.writeText(d.buildLog);
    setCopied(true);
    setTimeout(() => setCopied(false), 1200);
  }

  return (
    <Sheet
      onClose={onClose}
      title="Deployment"
      footer={
        <button
          type="button"
          className="btn-primary w-full"
          disabled={!canActivate || activate.isPending}
          onClick={() => activate.mutate(deploymentId)}
        >
          {activate.isPending ? <Spinner /> : isActive ? "Active" : "Activate"}
        </button>
      }
    >
      <div className="space-y-4 text-sm">
        <div className="flex items-center gap-2">
          <DeploymentStatusBadge status={d.status} />
          {/* Which deployment is live on the site's production URL vs. merely buildable/previewable —
              activatedAt alone can't tell this apart (see the "active" column's own comment above). */}
          {isActive ? (
            <Badge tone="mint">active — production</Badge>
          ) : d.status === "ready" ? (
            <Badge tone="ink">previewable</Badge>
          ) : null}
          <span className="text-xs text-ink-500">{elapsedLabel}</span>
          {d.imageTag ? <span className="font-mono text-xs text-ink-400">{d.imageTag}</span> : null}
        </div>
        {d.error ? <ErrorNote message={d.error} /> : null}
        {!showSuccess && d.previewUrl ? (
          <div className="rounded-lg border border-ink-800 bg-ink-900 px-3.5 py-2.5 text-xs text-ink-400">
            {canActivate ? "Not live yet — " : ""}
            <a
              href={d.previewUrl}
              target="_blank"
              rel="noreferrer"
              className="text-iris-300 hover:text-iris-200 hover:underline"
            >
              {canActivate ? "preview this build ↗" : "open its preview URL ↗"}
            </a>
            {canActivate ? " before activating it." : ""}
          </div>
        ) : null}

        {showSuccess ? (
          <DeploymentSuccessView
            deployment={d}
            publicUrl={sitePublicUrl}
            onViewLog={() => setView("log")}
            onSetEnvVar={() => {
              onClose();
              void navigate({ to: "/project/$projectId/sites/$siteId/settings", params: { projectId, siteId } });
            }}
            onDeployAgain={onDeployAgain}
          />
        ) : (
          <div>
            {isTrulyLive ? (
              <button
                type="button"
                className="mb-2 text-xs text-ink-500 hover:text-ink-300"
                onClick={() => setView("auto")}
              >
                ← Back to summary
              </button>
            ) : null}
            <div className="mb-1 flex items-center justify-between gap-2">
              <span className="text-xs font-medium uppercase tracking-wide text-ink-500">
                Build log {isLiveBuild ? "(live)" : ""}
              </span>
              <div className="flex items-center gap-2">
                <input
                  type="search"
                  placeholder="Search log…"
                  value={logSearch}
                  onChange={(e) => setLogSearch(e.target.value)}
                  className="w-32 rounded-md border border-ink-700 bg-ink-950 px-2 py-1 text-xs text-ink-100 placeholder:text-ink-500 outline-none focus:border-iris-500 focus:ring-2 focus:ring-iris-500/30"
                />
                <button
                  type="button"
                  className="btn-ghost shrink-0 border border-ink-700 px-2 py-1 text-[11px]"
                  disabled={!d.buildLog}
                  onClick={onCopyLog}
                >
                  {copied ? "Copied" : "Copy"}
                </button>
              </div>
            </div>
            <BuildLogView log={d.buildLog} search={logSearch} />
          </div>
        )}
      </div>
    </Sheet>
  );
}

/** Build log body: filters to matching lines and highlights the query when `search` is non-empty. */
function BuildLogView({ log, search }: { log: string; search: string }) {
  const query = search.trim();
  const lines = log.split("\n");
  const filtered = query ? lines.filter((line) => line.toLowerCase().includes(query.toLowerCase())) : lines;

  return (
    <pre className="max-h-96 overflow-auto rounded-lg border border-ink-700 bg-ink-950 px-4 py-3 font-mono text-xs whitespace-pre-wrap text-ink-300">
      {!log ? (
        "Waiting for the build to start…"
      ) : filtered.length === 0 ? (
        <span className="text-ink-600">No lines match "{query}".</span>
      ) : (
        filtered.map((line, i) => <div key={i}>{highlightLine(line, query)}</div>)
      )}
    </pre>
  );
}

function highlightLine(line: string, query: string): ReactNode {
  if (!query) return line || " ";
  const escaped = query.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  const parts = line.split(new RegExp(`(${escaped})`, "gi"));
  return parts.map((part, i) =>
    part.toLowerCase() === query.toLowerCase() ? (
      <mark key={i} className="rounded-sm bg-iris-500/40 text-ink-50">{part}</mark>
    ) : (
      part
    ),
  );
}

function formatSourceSize(bytes: number): string {
  if (bytes < 1024 * 1024) return `${Math.ceil(bytes / 1024)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

/** The "it's live" moment: build metrics, a link to the running site, and contextual next steps. */
function DeploymentSuccessView({
  deployment,
  publicUrl,
  onViewLog,
  onSetEnvVar,
  onDeployAgain,
}: {
  deployment: SiteDeployment;
  publicUrl: string;
  onViewLog: () => void;
  onSetEnvVar: () => void;
  onDeployAgain: () => void;
}) {
  const buildSeconds = deployment.activatedAt
    ? (new Date(deployment.activatedAt).getTime() - new Date(deployment.createdAt).getTime()) / 1000
    : null;

  return (
    <div className="space-y-5">
      <div className="rounded-lg border border-mint-400/30 bg-mint-400/5 px-5 py-6 text-center">
        <div className="mx-auto mb-3 grid size-9 place-items-center rounded-full bg-mint-400/15 text-mint-400">
          ✓
        </div>
        <p className="text-base font-medium text-ink-100">Your site is live</p>
        <p className="mt-1 text-xs text-ink-500">
          {buildSeconds !== null ? `Built in ${formatElapsed(buildSeconds)} · ` : null}
          {formatSourceSize(deployment.sourceSizeBytes)}
        </p>
        <a href={publicUrl} target="_blank" rel="noreferrer" className="btn-primary mt-4 inline-flex">
          Visit site ↗
        </a>
      </div>

      <div className="space-y-2">
        <span className="block text-xs font-medium uppercase tracking-wide text-ink-500">Next steps</span>
        <NextStepCard
          label="Set an environment variable"
          description="Configure secrets or config for your app."
          onClick={onSetEnvVar}
        />
        <NextStepCard
          label="View build log"
          description="See exactly what happened during the build."
          onClick={onViewLog}
        />
        <NextStepCard label="Deploy again" description="Upload a new build of your app." onClick={onDeployAgain} />
      </div>
    </div>
  );
}

function NextStepCard({ label, description, onClick }: { label: string; description: string; onClick: () => void }) {
  return (
    <button
      type="button"
      onClick={onClick}
      className="flex w-full items-center justify-between gap-3 rounded-lg border border-ink-800 bg-ink-900 px-3.5 py-2.5 text-left transition-colors hover:border-ink-600 hover:bg-ink-850"
    >
      <span>
        <span className="block text-sm font-medium text-ink-100">{label}</span>
        <span className="block text-xs text-ink-500">{description}</span>
      </span>
      <span className="text-ink-600">→</span>
    </button>
  );
}
