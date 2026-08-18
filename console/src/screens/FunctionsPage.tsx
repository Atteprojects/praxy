import { Link, useParams } from "@tanstack/react-router";
import { useState, type FormEvent } from "react";
import { useCreateFunction, useFunctions } from "../api/functions";
import { ApiError } from "../api/client";
import { FUNCTION_RUNTIMES, type FunctionRuntime } from "../api/types";
import {
  Badge, DataTable, EmptyState, ErrorNote, Field, FullPageSpinner, IdChip, Modal, Spinner, timeAgo,
} from "../components/ui";

const EVENT_PRESETS = [
  { pattern: "databases.*.tables.*.rows.*.create", label: "Row created" },
  { pattern: "databases.*.tables.*.rows.*.update", label: "Row updated" },
  { pattern: "databases.*.tables.*.rows.*.delete", label: "Row deleted" },
] as const;

// Kept in sync with docs/functions-runtimes.md — the Dart signature is deliberately `handler`, not
// `main`: Dart rejects any custom-signature top-level `main` anywhere in the compiled program, even
// one only ever reached via `import`, so the documented contract used to be unsatisfiable.
const RUNTIME_EXAMPLES: Record<FunctionRuntime, string> = {
  dart: `Future<Map<String, dynamic>> handler(Map<String, dynamic> context) async {
  return {'statusCode': 200, 'body': 'Hello, World!'};
}`,
  node: `module.exports = async (context) => ({
  statusCode: 200,
  body: 'Hello, World!',
});`,
};

const HEADERS = ["Name", "Runtime", "Triggers", "Status", "Created", ""];

export function FunctionsPage() {
  const { projectId } = useParams({ strict: false }) as { projectId: string };
  const fns = useFunctions(projectId);
  const [creating, setCreating] = useState(false);

  if (fns.isPending) return <FullPageSpinner />;
  if (fns.isError) throw fns.error;

  return (
    <div>
      <div className="mb-6 flex items-center justify-between">
        <h1 className="text-2xl font-semibold tracking-tight">Functions</h1>
        <button type="button" className="btn-primary" onClick={() => setCreating(true)}>
          + Create function
        </button>
      </div>

      {creating ? <CreateFunctionModal projectId={projectId} onClose={() => setCreating(false)} /> : null}

      {fns.data.total === 0 ? (
        <EmptyState
          headers={HEADERS}
          title="No functions yet. Create one, then deploy code to it from its Deployments tab."
          action={
            <button type="button" className="btn-primary" onClick={() => setCreating(true)}>
              + Create function
            </button>
          }
        />
      ) : (
        <DataTable headers={HEADERS}>
          {fns.data.functions.map((fn) => (
            <tr key={fn.id}>
              <td className="px-4 py-3">
                <Link
                  to="/project/$projectId/functions/$functionId"
                  params={{ projectId, functionId: fn.id }}
                  className="font-medium text-ink-100 hover:text-iris-300"
                >
                  {fn.name}
                </Link>
                <div className="mt-1"><IdChip id={fn.id} /></div>
              </td>
              <td className="px-4 py-3 font-mono text-xs text-ink-400">{fn.runtime}</td>
              <td className="px-4 py-3 text-xs text-ink-400">
                {[
                  fn.events.length > 0 ? `${fn.events.length} event(s)` : null,
                  fn.schedule ? `cron: ${fn.schedule}` : null,
                ].filter(Boolean).join(" · ") || "manual only"}
              </td>
              <td className="px-4 py-3">
                <div className="flex items-center gap-1.5">
                  {fn.enabled ? <Badge tone="mint">enabled</Badge> : <Badge tone="ink">disabled</Badge>}
                  {fn.activeDeploymentId ? (
                    fn.isWarm ? <Badge tone="iris">warm</Badge> : <Badge tone="ink">cold</Badge>
                  ) : (
                    <Badge tone="amber">no deployment</Badge>
                  )}
                </div>
              </td>
              <td className="px-4 py-3 whitespace-nowrap text-ink-400">{timeAgo(fn.createdAt)}</td>
              <td className="px-4 py-3 text-right whitespace-nowrap">
                <Link
                  to="/project/$projectId/functions/$functionId"
                  params={{ projectId, functionId: fn.id }}
                  className="btn-ghost border border-ink-700 px-2 py-1 text-xs"
                >
                  Open
                </Link>
              </td>
            </tr>
          ))}
        </DataTable>
      )}
    </div>
  );
}

function CreateFunctionModal({ projectId, onClose }: { projectId: string; onClose: () => void }) {
  const create = useCreateFunction(projectId);
  const [key, setKey] = useState("");
  const [keyTouched, setKeyTouched] = useState(false);
  const [name, setName] = useState("");
  const [runtime, setRuntime] = useState<FunctionRuntime>("dart");
  const [entrypoint, setEntrypoint] = useState("main.dart");
  const [timeoutSeconds, setTimeoutSeconds] = useState(15);
  const [events, setEvents] = useState<string[]>([]);
  const [schedule, setSchedule] = useState("");
  const error = create.error instanceof ApiError ? create.error : null;

  function slugify(value: string) {
    return value.toLowerCase().replace(/[^a-z0-9-]/g, "-").replace(/-+/g, "-").slice(0, 36) || "function";
  }

  function onRuntimeChange(next: FunctionRuntime) {
    setRuntime(next);
    setEntrypoint(next === "dart" ? "main.dart" : "index.js");
  }

  async function onSubmit(e: FormEvent) {
    e.preventDefault();
    await create.mutateAsync({
      key: key || slugify(name),
      name,
      runtime,
      entrypoint,
      timeoutSeconds,
      events,
      schedule: schedule.trim() || undefined,
    });
    onClose();
  }

  return (
    <Modal title="Create function" onClose={onClose}>
      <form onSubmit={(e) => void onSubmit(e)} className="space-y-4">
        {error && !error.envelope.fields ? <ErrorNote message={error.message} /> : null}
        <Field label="Name" error={error?.fieldErrors("name")[0]}>
          <input
            className="input-base"
            required
            autoFocus
            value={name}
            onChange={(e) => {
              setName(e.target.value);
              if (!keyTouched) setKey(slugify(e.target.value));
            }}
            placeholder="Send welcome email"
          />
        </Field>
        <Field label="Key" error={error?.fieldErrors("key")[0]}>
          <input
            className="input-base font-mono"
            required
            value={key}
            onChange={(e) => {
              setKeyTouched(true);
              setKey(e.target.value);
            }}
            placeholder="send-welcome-email"
          />
        </Field>
        <Field label="Runtime" error={error?.fieldErrors("runtime")[0]}>
          <select
            className="input-base"
            value={runtime}
            onChange={(e) => onRuntimeChange(e.target.value as FunctionRuntime)}
          >
            {FUNCTION_RUNTIMES.map((r) => <option key={r} value={r}>{r}</option>)}
          </select>
        </Field>
        <Field label="Entrypoint" error={error?.fieldErrors("entrypoint")[0]}>
          <input
            className="input-base font-mono"
            required
            value={entrypoint}
            onChange={(e) => setEntrypoint(e.target.value)}
            placeholder={runtime === "dart" ? "main.dart" : "index.js"}
          />
        </Field>
        <div>
          <span className="mb-1.5 block text-xs font-medium uppercase tracking-wide text-ink-400">
            {entrypoint || (runtime === "dart" ? "main.dart" : "index.js")} must export
          </span>
          <pre className="overflow-x-auto rounded-lg border border-ink-700 bg-ink-950 px-3 py-2.5 font-mono text-xs text-ink-300">
            {RUNTIME_EXAMPLES[runtime]}
          </pre>
        </div>
        <Field label="Timeout (seconds)" error={error?.fieldErrors("timeoutSeconds")[0]}>
          <input
            className="input-base"
            type="number"
            min={1}
            max={900}
            required
            value={timeoutSeconds}
            onChange={(e) => setTimeoutSeconds(Number(e.target.value))}
          />
        </Field>

        <div>
          <span className="mb-1.5 block text-xs font-medium uppercase tracking-wide text-ink-400">
            Event triggers (optional)
          </span>
          <div className="grid grid-cols-1 gap-2">
            {EVENT_PRESETS.map((preset) => (
              <label
                key={preset.pattern}
                className={`flex cursor-pointer items-center gap-2 rounded-lg border px-3 py-2 text-sm transition-colors ${
                  events.includes(preset.pattern)
                    ? "border-iris-500/60 bg-iris-500/10 text-ink-100"
                    : "border-ink-700 text-ink-400 hover:border-ink-500"
                }`}
              >
                <input
                  type="checkbox"
                  className="hidden"
                  checked={events.includes(preset.pattern)}
                  onChange={(e) =>
                    setEvents((current) =>
                      e.target.checked ? [...current, preset.pattern] : current.filter((p) => p !== preset.pattern),
                    )
                  }
                />
                <span>{preset.label}</span>
                <span className="ml-auto font-mono text-[11px] text-ink-500">{preset.pattern}</span>
              </label>
            ))}
          </div>
        </div>

        <Field label="Cron schedule (optional)" error={error?.fieldErrors("schedule")[0]}>
          <input
            className="input-base font-mono text-xs"
            value={schedule}
            onChange={(e) => setSchedule(e.target.value)}
            placeholder="0 * * * * (every hour)"
          />
        </Field>

        <button type="submit" className="btn-primary w-full" disabled={create.isPending}>
          {create.isPending ? <Spinner /> : "Create function"}
        </button>
      </form>
    </Modal>
  );
}
