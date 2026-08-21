import { useNavigate, useParams } from "@tanstack/react-router";
import { useState } from "react";
import {
  useDeleteSite, useDeleteSiteEnvVar, useSite, useSiteEnvVars, useSetSiteEnvVar, useUpdateSite,
} from "../api/sites";
import { ApiError } from "../api/client";
import { ConfirmButton } from "../components/ConfirmButton";
import { ErrorNote, Field, FullPageSpinner, Spinner, Toggle } from "../components/ui";
import { SiteDetailHeader } from "./SiteDetailHeader";

export function SiteSettingsPage() {
  const { projectId, siteId } = useParams({ strict: false }) as { projectId: string; siteId: string };
  const navigate = useNavigate();
  const site = useSite(projectId, siteId);
  const envVars = useSiteEnvVars(projectId, siteId);
  const update = useUpdateSite(projectId, siteId);
  const setVar = useSetSiteEnvVar(projectId, siteId);
  const deleteVar = useDeleteSiteEnvVar(projectId, siteId);
  const deleteSite = useDeleteSite(projectId);

  const [rootDirectory, setRootDirectory] = useState<string | null>(null);
  const [newVarKey, setNewVarKey] = useState("");
  const [newVarValue, setNewVarValue] = useState("");
  const [confirmName, setConfirmName] = useState("");
  const error = update.error instanceof ApiError ? update.error : null;

  if (site.isPending || envVars.isPending) return <FullPageSpinner />;
  if (site.isError) throw site.error;
  if (envVars.isError) throw envVars.error;

  async function onDeleteSite() {
    await deleteSite.mutateAsync(siteId);
    await navigate({ to: "/project/$projectId/sites", params: { projectId } });
  }

  return (
    <div>
      <SiteDetailHeader projectId={projectId} site={site.data} active="settings" />

      <div className="max-w-2xl space-y-8">
        <section className="surface p-5">
          <h2 className="mb-4 text-sm font-medium text-ink-100">General</h2>
          {error ? <div className="mb-3"><ErrorNote message={error.message} /></div> : null}
          <div className="space-y-4">
            <Toggle
              checked={site.data.enabled}
              onChange={(value) => update.mutate({ enabled: value })}
              label="Enabled"
              description="A disabled site refuses public traffic and Caddy's on-demand TLS ask will reject it — its container is left as-is, not stopped."
            />
            <Field label="Root directory" error={error?.fieldErrors("rootDirectory")[0]}>
              <div className="flex gap-2">
                <input
                  className="input-base flex-1 font-mono text-xs"
                  value={rootDirectory ?? site.data.rootDirectory}
                  onChange={(e) => setRootDirectory(e.target.value)}
                  placeholder="Leave blank if next.config.js is at the tar's root"
                />
                <button
                  type="button"
                  className="btn-ghost shrink-0 border border-ink-700 text-xs"
                  disabled={rootDirectory === null || rootDirectory === site.data.rootDirectory}
                  onClick={() => rootDirectory !== null && update.mutate({ rootDirectory })}
                >
                  Save
                </button>
              </div>
              <span className="mt-1 block text-[11px] text-ink-500">
                Takes effect on the next deployment, not retroactively.
              </span>
            </Field>
          </div>
        </section>

        <section className="surface p-5">
          <h2 className="mb-1 text-sm font-medium text-ink-100">Environment variables</h2>
          <p className="mb-3 text-xs text-ink-500">
            Injected at both build time (as a Docker build arg — so <code className="text-ink-300">NEXT_PUBLIC_*</code> values
            get inlined) and container runtime. Values are encrypted at rest and never shown again after
            saving. Takes effect on the next deployment, not retroactively.
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
                        <span className="font-mono text-ink-300">{v.key}</span> disappears from the next
                        deployment onward. Its value is not recoverable — you would have to paste it in
                        again.
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
            Deleting <span className="font-mono text-ink-300">{site.data.name}</span> stops its running
            container and removes every deployment record. Type its name to confirm.
          </p>
          {deleteSite.error instanceof ApiError ? <div className="mb-3"><ErrorNote message={deleteSite.error.message} /></div> : null}
          <div className="flex gap-2">
            <input
              className="input-base flex-1"
              value={confirmName}
              onChange={(e) => setConfirmName(e.target.value)}
              placeholder={site.data.name}
            />
            <button
              type="button"
              className="btn-ghost shrink-0 border border-coral-400/60 text-coral-400 disabled:opacity-40"
              disabled={confirmName !== site.data.name || deleteSite.isPending}
              onClick={() => void onDeleteSite()}
            >
              {deleteSite.isPending ? <Spinner /> : "Delete site"}
            </button>
          </div>
        </section>
      </div>
    </div>
  );
}
