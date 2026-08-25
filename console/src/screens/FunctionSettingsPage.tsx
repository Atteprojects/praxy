import { Link, useNavigate, useParams } from "@tanstack/react-router";
import { useState } from "react";
import {
  useConnectFunctionGit, useDeleteEnvVar, useDeleteFunction, useDisconnectFunctionGit, useFunction,
  useFunctionEnvVars, useFunctionGitBranches, useSetEnvVar, useUpdateFunction,
} from "../api/functions";
import { useGithubInstallations } from "../api/vcs";
import { ApiError } from "../api/client";
import { ConfirmButton } from "../components/ConfirmButton";
import { AddRoleButton, RoleLabel } from "../components/RolePicker";
import { Badge, ErrorNote, Field, FullPageSpinner, Spinner, Toggle } from "../components/ui";
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
  const installations = useGithubInstallations();
  const connectGit = useConnectFunctionGit(projectId, functionId);
  const disconnectGit = useDisconnectFunctionGit(projectId, functionId);

  const [entrypoint, setEntrypoint] = useState<string | null>(null);
  const [timeoutSeconds, setTimeoutSeconds] = useState<number | null>(null);
  const [schedule, setSchedule] = useState<string | null>(null);
  const [newVarKey, setNewVarKey] = useState("");
  const [newVarValue, setNewVarValue] = useState("");
  const [confirmName, setConfirmName] = useState("");
  const [repoInput, setRepoInput] = useState("");
  const [branchInput, setBranchInput] = useState("");
  const error = update.error instanceof ApiError ? update.error : null;
  const gitError = connectGit.error instanceof ApiError ? connectGit.error : null;
  const branches = useFunctionGitBranches(projectId, functionId, repoInput.trim());

  if (fn.isPending || envVars.isPending || installations.isPending) return <FullPageSpinner />;
  if (fn.isError) throw fn.error;
  if (envVars.isError) throw envVars.error;
  if (installations.isError) throw installations.error;

  const events = fn.data.events;
  const execute = fn.data.execute;

  function toggleEvent(pattern: string, enabled: boolean) {
    update.mutate({ events: enabled ? [...events, pattern] : events.filter((p) => p !== pattern) });
  }

  function addExecuteRole(role: string) {
    if (execute.includes(role)) return;
    update.mutate({ execute: [...execute, role] });
  }

  function removeExecuteRole(role: string) {
    update.mutate({ execute: execute.filter((r) => r !== role) });
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
          <h2 className="mb-1 text-sm font-medium text-ink-100">Execute access</h2>
          <p className="mb-3 text-xs text-ink-500">
            Who may invoke this function through the API
            (<span className="font-mono">POST /v1/functions/{fn.data.id}/executions</span>). Invoking
            from this console, event triggers and the schedule are unaffected.
          </p>

          {execute.length === 0 ? (
            <div className="mb-3 rounded-lg border border-amber-400/30 bg-amber-400/10 px-3 py-2 text-sm text-amber-400">
              No roles granted — the API refuses every invocation of this function. Add a role below
              to make it reachable.
            </div>
          ) : null}

          <div className="divide-y divide-ink-800/60">
            {execute.map((role) => (
              <div key={role} className="flex items-center justify-between gap-3 py-2">
                <RoleLabel projectId={projectId} role={role} />
                <button
                  type="button"
                  className="shrink-0 text-xs text-ink-500 hover:text-coral-400 cursor-pointer"
                  onClick={() => removeExecuteRole(role)}
                  aria-label={`Remove ${role}`}
                >
                  ✕
                </button>
              </div>
            ))}
          </div>

          <div className="mt-4 flex justify-end">
            <AddRoleButton projectId={projectId} existingRoles={execute} onPick={addExecuteRole} />
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

        <section className="surface p-5">
          <h2 className="mb-1 text-sm font-medium text-ink-100">Git repository</h2>
          <p className="mb-3 text-xs text-ink-500">
            Connect a GitHub repository to push-to-deploy: a push to the production branch builds and
            activates automatically, just like an upload does today. A push to any other branch builds
            a deployment too, but it stays <span className="font-mono text-ink-300">ready</span> without
            activating — production stays untouched.
          </p>

          {installations.data.total === 0 ? (
            <p className="text-xs text-ink-500">
              No GitHub App is connected to this instance yet.{" "}
              <Link to="/project/$projectId/github" params={{ projectId }} className="text-ink-300 underline">
                Connect one in Settings → GitHub
              </Link>{" "}
              first.
            </p>
          ) : fn.data.repositoryFullName ? (
            <div className="flex items-center justify-between rounded-lg border border-ink-800 bg-ink-900 px-3 py-2">
              <div className="flex items-center gap-2">
                <span className="font-mono text-xs text-ink-200">{fn.data.repositoryFullName}</span>
                <Badge>{fn.data.productionBranch}</Badge>
              </div>
              <ConfirmButton
                label="Disconnect"
                title="Disconnect repository?"
                confirmLabel="Disconnect"
                successMessage="Repository disconnected."
                className="text-xs text-ink-500 hover:text-coral-400 cursor-pointer"
                body={
                  <>
                    Pushes to <span className="font-mono text-ink-300">{fn.data.repositoryFullName}</span>{" "}
                    stop deploying this function. Its commits and branches on GitHub are untouched — this
                    only disconnects Praxy's side.
                  </>
                }
                onConfirm={() => disconnectGit.mutateAsync()}
              />
            </div>
          ) : (
            <div className="space-y-2">
              {gitError ? <ErrorNote message={gitError.message} /> : null}
              <input
                className="input-base w-full font-mono text-xs"
                placeholder="owner/repo"
                value={repoInput}
                onChange={(e) => {
                  setRepoInput(e.target.value);
                  setBranchInput("");
                }}
              />
              <div className="flex gap-2">
                <select
                  className="input-base flex-1 text-xs"
                  value={branchInput}
                  onChange={(e) => setBranchInput(e.target.value)}
                  disabled={!branches.data || branches.data.branches.length === 0}
                >
                  <option value="">
                    {branches.isFetching
                      ? "Loading branches…"
                      : branches.data
                        ? "Select the production branch"
                        : "Enter a repository above"}
                  </option>
                  {branches.data?.branches.map((b) => (
                    <option key={b} value={b}>{b}</option>
                  ))}
                </select>
                <button
                  type="button"
                  className="btn-ghost shrink-0 border border-ink-700 text-xs"
                  disabled={!branchInput || connectGit.isPending}
                  onClick={() =>
                    void connectGit.mutateAsync({ repositoryFullName: repoInput.trim(), productionBranch: branchInput })
                  }
                >
                  {connectGit.isPending ? <Spinner /> : "Connect"}
                </button>
              </div>
              {branches.isError ? (
                <p className="text-[11px] text-coral-400">
                  {branches.error instanceof ApiError ? branches.error.message : "Couldn't load branches."}
                </p>
              ) : null}
            </div>
          )}
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
