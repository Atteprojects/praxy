import { useParams } from "@tanstack/react-router";
import { useMemo, useState } from "react";
import { useFunction, useFunctionExecution, useFunctionExecutions, useInvokeFunction } from "../api/functions";
import { ApiError } from "../api/client";
import type { DataGridColumn } from "../components/DataGrid";
import { DataGrid } from "../components/DataGrid";
import { Badge, EmptyState, ErrorNote, Field, FullPageSpinner, Modal, Sheet, Spinner, timeAgo } from "../components/ui";
import type { FunctionExecution, FunctionExecutionStatus } from "../api/types";
import { FunctionDetailHeader } from "./FunctionDetailHeader";

const HEADERS = ["Time", "Trigger", "Status", "Duration", "Code", ""];

export function FunctionExecutionsPage() {
  const { projectId, functionId } = useParams({ strict: false }) as { projectId: string; functionId: string };
  const fn = useFunction(projectId, functionId);
  const executions = useFunctionExecutions(projectId, functionId);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [running, setRunning] = useState(false);

  const columns = useMemo<DataGridColumn<FunctionExecution>[]>(() => [
    {
      id: "time",
      header: "Time",
      cell: ({ row }) => <span className="text-xs text-ink-400">{timeAgo(row.original.createdAt)}</span>,
    },
    {
      id: "trigger",
      header: "Trigger",
      cell: ({ row }) => (
        <span className="text-xs text-ink-400">
          {row.original.trigger}{row.original.async ? " · async" : ""}
          {row.original.coldStart ? " · cold" : ""}
        </span>
      ),
    },
    {
      id: "status",
      header: "Status",
      cell: ({ row }) => <ExecutionStatusBadge status={row.original.status} />,
    },
    {
      id: "duration",
      header: "Duration",
      cell: ({ row }) => (
        <span className="text-xs text-ink-400">{row.original.durationMs !== null ? `${row.original.durationMs}ms` : "—"}</span>
      ),
    },
    {
      id: "code",
      header: "Code",
      cell: ({ row }) => <span className="font-mono text-xs text-ink-400">{row.original.statusCode ?? "—"}</span>,
    },
    {
      id: "triggeredBy",
      header: "",
      cell: ({ row }) => (
        <span className="font-mono text-[11px] text-ink-500">{row.original.triggeredBy ?? ""}</span>
      ),
    },
  ], []);

  if (fn.isPending || executions.isPending) return <FullPageSpinner />;
  if (fn.isError) throw fn.error;
  if (executions.isError) throw executions.error;

  return (
    <div>
      <FunctionDetailHeader projectId={projectId} fn={fn.data} active="executions" />

      <div className="mb-4 flex justify-end">
        <button
          type="button"
          className="btn-primary"
          disabled={!fn.data.activeDeploymentId}
          title={!fn.data.activeDeploymentId ? "Deploy a build first" : undefined}
          onClick={() => setRunning(true)}
        >
          Run
        </button>
      </div>

      {running ? (
        <RunModal
          projectId={projectId}
          functionId={functionId}
          onClose={() => setRunning(false)}
          onInvoked={(id) => setSelectedId(id)}
        />
      ) : null}

      {executions.data.total === 0 ? (
        <EmptyState headers={HEADERS} title="No executions yet — run the function or wait for a trigger to fire." />
      ) : (
        <DataGrid
          columns={columns}
          data={executions.data.executions}
          getRowId={(row) => row.id}
          onRowClick={(row) => setSelectedId(row.id)}
          maxHeight="65vh"
        />
      )}

      {selectedId ? (
        <ExecutionSheet
          projectId={projectId}
          functionId={functionId}
          executionId={selectedId}
          onClose={() => setSelectedId(null)}
        />
      ) : null}
    </div>
  );
}

function ExecutionStatusBadge({ status }: { status: FunctionExecutionStatus }) {
  if (status === "completed") return <Badge tone="mint">completed</Badge>;
  if (status === "failed") return <Badge tone="coral">failed</Badge>;
  if (status === "processing") return <Badge tone="amber">processing</Badge>;
  return <Badge tone="ink">waiting</Badge>;
}

function RunModal({
  projectId,
  functionId,
  onClose,
  onInvoked,
}: {
  projectId: string;
  functionId: string;
  onClose: () => void;
  onInvoked: (executionId: string) => void;
}) {
  const invoke = useInvokeFunction(projectId, functionId);
  const [method, setMethod] = useState("GET");
  const [path, setPath] = useState("/");
  const [body, setBody] = useState("");
  const [async_, setAsync] = useState(false);
  const error = invoke.error instanceof ApiError ? invoke.error : null;

  async function onSubmit() {
    const execution = await invoke.mutateAsync({ method, path, body: body || undefined, async: async_ });
    onInvoked(execution.id);
    onClose();
  }

  return (
    <Modal title="Run function" onClose={onClose}>
      <div className="space-y-4">
        {error ? <ErrorNote message={error.message} /> : null}
        <div className="flex gap-2">
          <Field label="Method">
            <select className="input-base" value={method} onChange={(e) => setMethod(e.target.value)}>
              {["GET", "POST", "PUT", "PATCH", "DELETE"].map((m) => <option key={m} value={m}>{m}</option>)}
            </select>
          </Field>
          <div className="flex-1">
            <Field label="Path">
              <input className="input-base font-mono" value={path} onChange={(e) => setPath(e.target.value)} />
            </Field>
          </div>
        </div>
        <Field label="Body (optional)">
          <textarea
            className="input-base h-24 font-mono text-xs"
            value={body}
            onChange={(e) => setBody(e.target.value)}
            placeholder="Raw request body passed to the function"
          />
        </Field>
        <label className="flex items-center gap-2 text-sm text-ink-300">
          <input type="checkbox" className="accent-iris-500" checked={async_} onChange={(e) => setAsync(e.target.checked)} />
          Run asynchronously (don't wait for the result)
        </label>
        <button type="button" className="btn-primary w-full" disabled={invoke.isPending} onClick={() => void onSubmit()}>
          {invoke.isPending ? <Spinner /> : "Run"}
        </button>
      </div>
    </Modal>
  );
}

function ExecutionSheet({
  projectId,
  functionId,
  executionId,
  onClose,
}: {
  projectId: string;
  functionId: string;
  executionId: string;
  onClose: () => void;
}) {
  const detail = useFunctionExecution(projectId, functionId, executionId);

  if (detail.isPending) {
    return (
      <Sheet onClose={onClose} title="Execution">
        <div className="grid place-items-center py-10"><Spinner /></div>
      </Sheet>
    );
  }
  if (detail.isError) throw detail.error;

  const e = detail.data;
  return (
    <Sheet onClose={onClose} title="Execution">
      <div className="space-y-4 text-sm">
        <div className="flex flex-wrap items-center gap-2">
          <ExecutionStatusBadge status={e.status} />
          <span className="text-xs text-ink-400">{e.method} {e.path}</span>
          {e.statusCode !== null ? <span className="font-mono text-xs text-ink-400">HTTP {e.statusCode}</span> : null}
          {e.durationMs !== null ? <span className="text-xs text-ink-500">{e.durationMs}ms</span> : null}
          {e.coldStart ? <Badge tone="ink">cold start</Badge> : null}
        </div>
        {e.triggeredBy ? (
          <p className="text-xs text-ink-500">
            Triggered by <span className="font-mono break-all text-ink-300">{e.triggeredBy}</span>
          </p>
        ) : null}
        {e.errors ? (
          <div>
            <span className="mb-1 block text-xs font-medium uppercase tracking-wide text-coral-400">Errors</span>
            <pre className="max-h-48 overflow-auto rounded-lg border border-coral-400/30 bg-ink-950 px-4 py-3 font-mono text-xs whitespace-pre-wrap text-coral-300">
              {e.errors}
            </pre>
          </div>
        ) : null}
        <div>
          <span className="mb-1 block text-xs font-medium uppercase tracking-wide text-ink-500">Response body</span>
          <pre className="max-h-48 overflow-auto rounded-lg border border-ink-700 bg-ink-950 px-4 py-3 font-mono text-xs whitespace-pre-wrap text-ink-300">
            {e.responseBody || "(empty)"}
          </pre>
        </div>
        <div>
          <span className="mb-1 block text-xs font-medium uppercase tracking-wide text-ink-500">Logs</span>
          <pre className="max-h-48 overflow-auto rounded-lg border border-ink-700 bg-ink-950 px-4 py-3 font-mono text-xs whitespace-pre-wrap text-ink-300">
            {e.logs || "(no output)"}
          </pre>
        </div>
      </div>
    </Sheet>
  );
}
