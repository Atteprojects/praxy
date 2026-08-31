import { useParams } from "@tanstack/react-router";
import { useMemo, useRef, useState } from "react";
import {
  useActivateDeployment, useCreateDeployment, useFunction, useFunctionDeployment, useFunctionDeployments,
} from "../api/functions";
import { ApiError } from "../api/client";
import type { DataGridColumn } from "../components/DataGrid";
import { DataGrid } from "../components/DataGrid";
import { Badge, EmptyState, ErrorNote, FullPageSpinner, Sheet, Spinner, timeAgo } from "../components/ui";
import type { FunctionDeployment, FunctionDeploymentStatus } from "../api/types";
import { FunctionDetailHeader } from "./FunctionDetailHeader";

const HEADERS = ["Created", "Status", "Source", "Size", "Image", ""];

export function FunctionDeploymentsPage() {
  const { projectId, functionId } = useParams({ strict: false }) as { projectId: string; functionId: string };
  const fn = useFunction(projectId, functionId);
  const deployments = useFunctionDeployments(projectId, functionId);
  const create = useCreateDeployment(projectId, functionId);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const fileInput = useRef<HTMLInputElement>(null);
  const uploadError = create.error instanceof ApiError ? create.error : null;

  const activeDeploymentId = fn.data?.activeDeploymentId ?? null;

  const columns = useMemo<DataGridColumn<FunctionDeployment>[]>(() => [
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
      id: "source",
      header: "Source",
      cell: ({ row }) => {
        const d = row.original;
        if (d.source !== "git") return <span className="text-xs text-ink-600">upload</span>;
        return (
          <span className="font-mono text-xs text-ink-400" title={d.commitMessage ?? undefined}>
            {d.branch}
            {d.commitSha ? ` @ ${d.commitSha.slice(0, 7)}` : ""}
          </span>
        );
      },
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
      // The function's currently active deployment, not merely "has been activated at some point" —
      // activatedAt stays set on a deployment forever once it's first activated, even after a
      // redeploy supersedes it, so activatedAt alone can't tell "active" from "was active once."
      cell: ({ row }) => (row.original.id === activeDeploymentId ? <Badge tone="mint">active</Badge> : null),
    },
  ], [activeDeploymentId]);

  if (fn.isPending || deployments.isPending) return <FullPageSpinner />;
  if (fn.isError) throw fn.error;
  if (deployments.isError) throw deployments.error;

  async function onFileChosen(file: File | undefined) {
    if (!file) return;
    const created = await create.mutateAsync(file);
    setSelectedId(created.id);
    if (fileInput.current) fileInput.current.value = "";
  }

  return (
    <div>
      <FunctionDetailHeader projectId={projectId} fn={fn.data} active="deployments" />

      <div className="mb-4 flex items-center justify-between">
        <p className="max-w-xl text-xs text-ink-500">
          Upload a <code className="text-ink-300">.tar</code> of your function's source (its root must
          contain <code className="text-ink-300">{fn.data.entrypoint}</code>). A successful build
          activates automatically; older ready builds can be re-activated below.
        </p>
        <div>
          <input
            ref={fileInput}
            type="file"
            accept=".tar,.tar.gz,.tgz"
            className="hidden"
            onChange={(e) => void onFileChosen(e.target.files?.[0])}
          />
          <button
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

      {deployments.data.total === 0 ? (
        <EmptyState headers={HEADERS} title="No deployments yet. Upload a tar to build the first one." />
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
          projectId={projectId}
          functionId={functionId}
          deploymentId={selectedId}
          activeDeploymentId={activeDeploymentId}
          onClose={() => setSelectedId(null)}
        />
      ) : null}
    </div>
  );
}

function DeploymentStatusBadge({ status }: { status: FunctionDeploymentStatus }) {
  if (status === "ready") return <Badge tone="mint">ready</Badge>;
  if (status === "failed") return <Badge tone="coral">failed</Badge>;
  if (status === "building") return <Badge tone="amber">building</Badge>;
  return <Badge tone="ink">queued</Badge>;
}

function DeploymentSheet({
  projectId,
  functionId,
  deploymentId,
  activeDeploymentId,
  onClose,
}: {
  projectId: string;
  functionId: string;
  deploymentId: string;
  activeDeploymentId: string | null;
  onClose: () => void;
}) {
  const detail = useFunctionDeployment(projectId, functionId, deploymentId);
  const activate = useActivateDeployment(projectId, functionId);

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
  // "Ready but not the function's current deployment" is what makes a deployment activatable — a
  // superseded deployment keeps its own activatedAt timestamp forever, so that alone can't
  // distinguish "currently active" from "was active once" (this is exactly what makes rollback via
  // this button possible after a redeploy).
  const canActivate = d.status === "ready" && !isActive;

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
          {d.imageTag ? <span className="font-mono text-xs text-ink-400">{d.imageTag}</span> : null}
        </div>
        {d.source === "git" ? (
          <div className="text-xs text-ink-500">
            pushed to <span className="font-mono text-ink-300">{d.branch}</span>
            {d.commitSha ? (
              <>
                {" "}
                @ <span className="font-mono text-ink-300">{d.commitSha.slice(0, 7)}</span>
              </>
            ) : null}
            {d.commitMessage ? <span> — {d.commitMessage}</span> : null}
          </div>
        ) : null}
        {d.error ? <ErrorNote message={d.error} /> : null}
        <div>
          <span className="mb-1 block text-xs font-medium uppercase tracking-wide text-ink-500">
            Build log {d.status === "building" || d.status === "queued" ? "(live)" : ""}
          </span>
          <pre className="max-h-96 overflow-auto rounded-lg border border-ink-700 bg-ink-950 px-4 py-3 font-mono text-xs whitespace-pre-wrap text-ink-300">
            {d.buildLog || "Waiting for the build to start…"}
          </pre>
        </div>
      </div>
    </Sheet>
  );
}
