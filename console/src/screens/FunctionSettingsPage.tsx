import { useNavigate, useParams } from "@tanstack/react-router";
import { useState } from "react";
import {
  useDeleteEnvVar, useDeleteFunction, useFunction, useFunctionEnvVars, useSetEnvVar, useUpdateFunction,
} from "../api/functions";
import { ApiError } from "../api/client";
import { ConfirmButton } from "../components/ConfirmButton";
import { ErrorNote, Field, FullPageSpinner, Spinner, Toggle } from "../components/ui";
import { FunctionDetailHeader } from "./FunctionDetailHeader";

const EVENT_PRESETS = [
  { pattern: "databases.*.tables.*.rows.*.create", label: "Row created" },
  { pattern: "databases.*.tables.*.rows.*.update", label: "Row updated" },
  { pattern: "databases.*.tables.*.rows.*.delete", label: "Row deleted" },
] as const;

export function FunctionSettingsPage() {
  const { projectId, functionId } = useParams({ strict: false }) as { projectId: string; functionId: string };
  const navigate = useNavigate();
  const fn = useFunction(projectId, functionId);
  const envVars = useFunctionEnvVars(projectId, functionId);
  const update = useUpdateFunction(projectId, functionId);
  const setVar = useSetEnvVar(projectId, functionId);
  const deleteVar = useDeleteEnvVar(projectId, functionId);
  const deleteFn = useDeleteFunction(projectId);

  const [entrypoint, setEntrypoint] = useState<string | null>(null);
  const [timeoutSeconds, setTimeoutSeconds] = useState<number | null>(null);
  const [schedule, setSchedule] = useState<string | null>(null);
  const [newVarKey, setNewVarKey] = useState("");
  const [newVarValue, setNewVarValue] = useState("");
  const [confirmName, setConfirmName] = useState("");
  const error = update.error instanceof ApiError ? update.error : null;

  if (fn.isPending || envVars.isPending) return <FullPageSpinner />;
  if (fn.isError) throw fn.error;
  if (envVars.isError) throw envVars.error;

  const events = fn.data.events;

  function toggleEvent(pattern: string, enabled: boolean) {
    update.mutate({ events: enabled ? [...events, pattern] : events.filter((p) => p !== pattern) });
  }

  async function onDeleteFunction() {
    await deleteFn.mutateAsync(functionId);
    await navigate({ to: "/project/$projectId/functions", params: { projectId } });
  }

  return (
    <div>
      <FunctionDetailHeader projectId={projectId} fn={fn.data} active="settings" />

      <div className="max-w-2xl space-y-8">
        <section className="surface p-5">
          <h2 className="mb-4 text-sm font-medium text-ink-100">General</h2>
          {error ? <div className="mb-3"><ErrorNote message={error.message} /></div> : null}
          <div className="space-y-4">
            <Toggle
              checked={fn.data.enabled}
              onChange={(value) => update.mutate({ enabled: value })}
              label="Enabled"
              description="Disabled functions refuse invocations and never fire on triggers or schedule."
            />
            <Field label="Entrypoint">
              <div className="flex gap-2">
                <input
                  className="input-base flex-1 font-mono"
                  value={entrypoint ?? fn.data.entrypoint}
                  onChange={(e) => setEntrypoint(e.target.value)}
                />
                <button
                  type="button"
                  className="btn-ghost shrink-0 border border-ink-700 text-xs"
                  disabled={entrypoint === null || entrypoint === fn.data.entrypoint}
                  onClick={() => entrypoint && update.mutate({ entrypoint })}
                >
                  Save
                </button>
              </div>
            </Field>
            <Field label="Timeout (seconds)">
              <div className="flex gap-2">
                <input
                  className="input-base flex-1"
                  type="number"
                  min={1}
                  max={900}
                  value={timeoutSeconds ?? fn.data.timeoutSeconds}
                  onChange={(e) => setTimeoutSeconds(Number(e.target.value))}
                />
                <button
                  type="button"
                  className="btn-ghost shrink-0 border border-ink-700 text-xs"
                  disabled={timeoutSeconds === null || timeoutSeconds === fn.data.timeoutSeconds}
                  onClick={() => timeoutSeconds && update.mutate({ timeoutSeconds })}
                >
                  Save
                </button>
              </div>
            </Field>
          </div>
        </section>

        <section className="surface p-5">
          <h2 className="mb-1 text-sm font-medium text-ink-100">Triggers</h2>
          <p className="mb-3 text-xs text-ink-500">
            Event triggers only fire on row events — the durable outbox only carries row writes today
            (docs/architecture.md §7). Function execution presets mirror webhooks' for the same reason.
          </p>
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
                  onChange={(e) => toggleEvent(preset.pattern, e.target.checked)}
                />
                <span>{preset.label}</span>
                <span className="ml-auto font-mono text-[11px] text-ink-500">{preset.pattern}</span>
              </label>
            ))}
          </div>
        </section>

        <section className="surface p-5">
          <h2 className="mb-1 text-sm font-medium text-ink-100">Schedule</h2>
          <p className="mb-3 text-xs text-ink-500">
            Standard 5-field cron, evaluated in UTC. Clear the field and save to unschedule.
          </p>
          <div className="flex gap-2">
            <input
              className="input-base flex-1 font-mono text-xs"
              value={schedule ?? fn.data.schedule ?? ""}
              onChange={(e) => setSchedule(e.target.value)}
              placeholder="0 * * * * (every hour)"
            />
            <button
              type="button"
              className="btn-ghost shrink-0 border border-ink-700 text-xs"
              disabled={schedule === null}
              onClick={() => update.mutate({ schedule: schedule ?? "" })}
            >
              Save
            </button>
          </div>
          {fn.data.nextScheduledRunAt ? (
            <p className="mt-2 text-xs text-ink-500">
              Next run: {new Date(fn.data.nextScheduledRunAt).toLocaleString()}
            </p>
          ) : null}
        </section>

        <section className="surface p-5">
          <h2 className="mb-1 text-sm font-medium text-ink-100">Environment variables</h2>
          <p className="mb-3 text-xs text-ink-500">
            Values are encrypted at rest and never shown again after saving — same reveal-once
            treatment as an API key secret.
          </p>
          <div className="mb-3 space-y-2">
            {envVars.data.total === 0 ? (
              <p className="text-xs text-ink-500">No environment variables set.</p>
            ) : (
              envVars.data.vars.map((v) => (
                <div key={v.key} className="flex items-center justify-between rounded-lg border border-ink-800 bg-ink-900 px-3 py-2">
                  <span className="font-mono text-xs text-ink-200">{v.key}</span>
                  <ConfirmButton
                    label="Remove"
                    title="Remove environment variable?"
                    confirmLabel="Remove variable"
                    successMessage={`Removed ${v.key}.`}
                    className="text-xs text-ink-500 hover:text-coral-400 cursor-pointer"
                    body={
                      <>
                        <span className="font-mono text-ink-300">{v.key}</span> disappears from the next execution
                        onward. Its value is not recoverable — you would have to paste it in again.
                      </>
                    }
                    onConfirm={() => deleteVar.mutateAsync(v.key)}
                  />
                </div>
              ))
            )}
          </div>
          <div className="flex gap-2">
            <input
              className="input-base flex-1 font-mono text-xs"
              placeholder="KEY"
              value={newVarKey}
              onChange={(e) => setNewVarKey(e.target.value.toUpperCase())}
            />
            <input
              className="input-base flex-1 font-mono text-xs"
              placeholder="value"
              type="password"
              value={newVarValue}
              onChange={(e) => setNewVarValue(e.target.value)}
            />
            <button
              type="button"
              className="btn-ghost shrink-0 border border-ink-700 text-xs"
              disabled={!newVarKey || !newVarValue || setVar.isPending}
              onClick={() => {
                setVar.mutate({ key: newVarKey, value: newVarValue });
                setNewVarKey("");
                setNewVarValue("");
              }}
            >
              {setVar.isPending ? <Spinner /> : "+ Add"}
            </button>
          </div>
        </section>

        <section className="surface border-coral-400/20 p-5">
          <h2 className="mb-3 text-sm font-medium text-coral-400">Danger zone</h2>
          <p className="mb-3 text-xs text-ink-500">
            Deleting <span className="font-mono text-ink-300">{fn.data.name}</span> removes every
            deployment and execution record. Type its name to confirm.
          </p>
          {deleteFn.error instanceof ApiError ? <div className="mb-3"><ErrorNote message={deleteFn.error.message} /></div> : null}
          <div className="flex gap-2">
            <input
              className="input-base flex-1"
              value={confirmName}
              onChange={(e) => setConfirmName(e.target.value)}
              placeholder={fn.data.name}
            />
            <button
              type="button"
              className="btn-ghost shrink-0 border border-coral-400/60 text-coral-400 disabled:opacity-40"
              disabled={confirmName !== fn.data.name || deleteFn.isPending}
              onClick={() => void onDeleteFunction()}
            >
              {deleteFn.isPending ? <Spinner /> : "Delete function"}
            </button>
          </div>
        </section>
      </div>
    </div>
  );
}
