import { Link, useNavigate, useParams } from "@tanstack/react-router";
import { useState } from "react";
import {
  useAddSiteDomain, useConnectSiteGit, useDeleteSite, useDeleteSiteDomain, useDeleteSiteEnvVar,
  useDisconnectSiteGit, useSite, useSiteDomains, useSiteEnvVars, useSiteGitBranches, useSetSiteEnvVar,
  useUpdateSite,
} from "../api/sites";
import { useGithubInstallations } from "../api/vcs";
import { ApiError } from "../api/client";
import { ConfirmButton } from "../components/ConfirmButton";
import { Badge, ErrorNote, Field, FullPageSpinner, Spinner, timeAgo, Toggle } from "../components/ui";
import { SiteDetailHeader } from "./SiteDetailHeader";

export function SiteSettingsPage() {
  const { projectId, siteId } = useParams({ strict: false }) as { projectId: string; siteId: string };
  const navigate = useNavigate();
  const site = useSite(projectId, siteId);
  const envVars = useSiteEnvVars(projectId, siteId);
  const domains = useSiteDomains(projectId, siteId);
  const update = useUpdateSite(projectId, siteId);
  const setVar = useSetSiteEnvVar(projectId, siteId);
  const deleteVar = useDeleteSiteEnvVar(projectId, siteId);
  const deleteSite = useDeleteSite(projectId);
  const addDomain = useAddSiteDomain(projectId, siteId);
  const deleteDomain = useDeleteSiteDomain(projectId, siteId);
  const installations = useGithubInstallations();
  const connectGit = useConnectSiteGit(projectId, siteId);
  const disconnectGit = useDisconnectSiteGit(projectId, siteId);

  const [rootDirectory, setRootDirectory] = useState<string | null>(null);
  const [newVarKey, setNewVarKey] = useState("");
  const [newVarValue, setNewVarValue] = useState("");
  const [newHostname, setNewHostname] = useState("");
  const [confirmName, setConfirmName] = useState("");
  const [repoInput, setRepoInput] = useState("");
  const [branchInput, setBranchInput] = useState("");
  const error = update.error instanceof ApiError ? update.error : null;
  const domainError = addDomain.error instanceof ApiError ? addDomain.error : null;
  const gitError = connectGit.error instanceof ApiError ? connectGit.error : null;
  const branches = useSiteGitBranches(projectId, siteId, repoInput.trim());

  if (site.isPending || envVars.isPending || domains.isPending || installations.isPending) return <FullPageSpinner />;
  if (site.isError) throw site.error;
  if (envVars.isError) throw envVars.error;
  if (domains.isError) throw domains.error;
  if (installations.isError) throw installations.error;

  // The CNAME target for a subdomain custom domain — the site's own always-on hostname, stripped of
  // scheme. Apex domains can't use CNAME (a DNS protocol limitation, not ours) and this instance
  // doesn't know its own public IP, so an A/AAAA record is left as self-serve/manual, documented
  // below rather than auto-filled.
  const cnameTarget = new URL(site.data.publicUrl).host;

  async function onAddDomain() {
    await addDomain.mutateAsync(newHostname.trim());
    setNewHostname("");
  }

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

        <section className="surface p-5">
          <h2 className="mb-1 text-sm font-medium text-ink-100">Custom domains</h2>
          <p className="mb-3 text-xs text-ink-500">
            Point your own domain at this site's <span className="font-mono text-ink-300">active</span> deployment
            — no preview URLs, one hostname per domain. For a subdomain of your own domain (e.g.{" "}
            <code className="text-ink-300">app.example.com</code>), add a{" "}
            <code className="text-ink-300">CNAME</code> record pointing at{" "}
            <code className="text-ink-300">{cnameTarget}</code>. An apex domain (e.g.{" "}
            <code className="text-ink-300">example.com</code>) can't use CNAME — point an{" "}
            <code className="text-ink-300">A</code>/<code className="text-ink-300">AAAA</code> record at this
            instance's own public IP instead. A domain shows <Badge tone="amber">pending</Badge> until the
            DNS record resolves and its first real visit succeeds, then flips to{" "}
            <Badge tone="mint">verified</Badge> on its own — no separate step.
          </p>
          <div className="mb-3 space-y-2">
            {domains.data.total === 0 ? (
              <p className="text-xs text-ink-500">No custom domains configured.</p>
            ) : (
              domains.data.domains.map((d) => (
                <div key={d.id} className="flex items-center justify-between rounded-lg border border-ink-800 bg-ink-900 px-3 py-2">
                  <div className="flex items-center gap-2">
                    <span className="font-mono text-xs text-ink-200">{d.hostname}</span>
                    {d.status === "verified" ? (
                      <Badge tone="mint">verified</Badge>
                    ) : (
                      <Badge tone="amber">pending</Badge>
                    )}
                    <span className="text-[11px] text-ink-500">
                      {d.status === "verified" && d.verifiedAt ? `verified ${timeAgo(d.verifiedAt)}` : `added ${timeAgo(d.createdAt)}`}
                    </span>
                  </div>
                  <ConfirmButton
                    label="Remove"
                    title="Remove custom domain?"
                    confirmLabel="Remove domain"
                    successMessage={`Removed ${d.hostname}.`}
                    className="text-xs text-ink-500 hover:text-coral-400 cursor-pointer"
                    body={
                      <>
                        <span className="font-mono text-ink-300">{d.hostname}</span> stops resolving to this
                        site immediately. Its DNS record isn't ours to remove — take that down separately if
                        you're retiring the domain entirely.
                      </>
                    }
                    onConfirm={() => deleteDomain.mutateAsync(d.id)}
                  />
                </div>
              ))
            )}
          </div>
          {domainError ? <div className="mb-3"><ErrorNote message={domainError.message} /></div> : null}
          <div className="flex gap-2">
            <input
              className="input-base flex-1 font-mono text-xs"
              placeholder="app.example.com"
              value={newHostname}
              onChange={(e) => setNewHostname(e.target.value)}
            />
            <button
              type="button"
              className="btn-ghost shrink-0 border border-ink-700 text-xs"
              disabled={!newHostname.trim() || addDomain.isPending}
              onClick={() => void onAddDomain()}
            >
              {addDomain.isPending ? <Spinner /> : "+ Add"}
            </button>
          </div>
        </section>

        <section className="surface p-5">
          <h2 className="mb-1 text-sm font-medium text-ink-100">Git repository</h2>
          <p className="mb-3 text-xs text-ink-500">
            Connect a GitHub repository to push-to-deploy: a push to the production branch builds and
            goes live automatically, just like an upload does today. A push to any other branch builds
            a deployment too, but only ever reaches its own preview URL — production stays untouched.
          </p>

          {installations.data.total === 0 ? (
            <p className="text-xs text-ink-500">
              No GitHub App is connected to this instance yet.{" "}
              <Link to="/project/$projectId/github" params={{ projectId }} className="text-ink-300 underline">
                Connect one in Settings → GitHub
              </Link>{" "}
              first.
            </p>
          ) : site.data.repositoryFullName ? (
            <div className="flex items-center justify-between rounded-lg border border-ink-800 bg-ink-900 px-3 py-2">
              <div className="flex items-center gap-2">
                <span className="font-mono text-xs text-ink-200">{site.data.repositoryFullName}</span>
                <Badge>{site.data.productionBranch}</Badge>
              </div>
              <ConfirmButton
                label="Disconnect"
                title="Disconnect repository?"
                confirmLabel="Disconnect"
                successMessage="Repository disconnected."
                className="text-xs text-ink-500 hover:text-coral-400 cursor-pointer"
                body={
                  <>
                    Pushes to <span className="font-mono text-ink-300">{site.data.repositoryFullName}</span>{" "}
                    stop deploying this site. Its commits and branches on GitHub are untouched — this only
                    disconnects Praxy's side.
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
