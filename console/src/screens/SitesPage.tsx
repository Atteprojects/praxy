import { Link, useParams } from "@tanstack/react-router";
import { useState, type FormEvent } from "react";
import { useCreateSite, useSites } from "../api/sites";
import { ApiError } from "../api/client";
import { Badge, DataTable, EmptyState, ErrorNote, Field, FullPageSpinner, IdChip, Modal, PageHeader, Spinner, timeAgo } from "../components/ui";

const HEADERS = ["Name", "Public URL", "Status", "Created", ""];

export function SitesPage() {
  const { projectId } = useParams({ strict: false }) as { projectId: string };
  const sites = useSites(projectId);
  const [creating, setCreating] = useState(false);

  if (sites.isPending) return <FullPageSpinner />;
  if (sites.isError) throw sites.error;

  return (
    <div>
      <PageHeader
        title="Sites"
        description="Next.js apps built from an uploaded tar and hosted on their own subdomain — the console's own tar-upload-and-build pipeline Functions already uses, but the resulting container stays running instead of being invoked."
        actions={
          <button type="button" className="btn-primary" onClick={() => setCreating(true)}>
            + Create site
          </button>
        }
      />

      {creating ? <CreateSiteModal projectId={projectId} onClose={() => setCreating(false)} /> : null}

      {sites.data.total === 0 ? (
        <EmptyState
          headers={HEADERS}
          title="No sites yet. Create one, then deploy a Next.js app to it from its Deployments tab."
          action={
            <button type="button" className="btn-primary" onClick={() => setCreating(true)}>
              + Create site
            </button>
          }
        />
      ) : (
        <DataTable headers={HEADERS}>
          {sites.data.sites.map((site) => (
            <tr key={site.id}>
              <td className="px-4 py-3">
                <Link
                  to="/project/$projectId/sites/$siteId"
                  params={{ projectId, siteId: site.id }}
                  className="font-medium text-ink-100 hover:text-iris-300"
                >
                  {site.name}
                </Link>
                <div className="mt-1"><IdChip id={site.id} /></div>
              </td>
              <td className="px-4 py-3 max-w-xs truncate font-mono text-xs text-ink-400">
                {site.activeDeploymentId && site.isRunning ? (
                  <a href={site.publicUrl} target="_blank" rel="noreferrer" className="text-iris-300 hover:underline">
                    {site.publicUrl}
                  </a>
                ) : (
                  site.publicUrl
                )}
              </td>
              <td className="px-4 py-3">
                <div className="flex items-center gap-1.5">
                  {site.enabled ? <Badge tone="mint">enabled</Badge> : <Badge tone="ink">disabled</Badge>}
                  {site.activeDeploymentId ? (
                    site.isRunning ? <Badge tone="mint">live</Badge> : <Badge tone="amber">starting</Badge>
                  ) : (
                    <Badge tone="amber">no deployment</Badge>
                  )}
                </div>
              </td>
              <td className="px-4 py-3 whitespace-nowrap text-ink-400">{timeAgo(site.createdAt)}</td>
              <td className="px-4 py-3 text-right whitespace-nowrap">
                <Link
                  to="/project/$projectId/sites/$siteId"
                  params={{ projectId, siteId: site.id }}
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

function CreateSiteModal({ projectId, onClose }: { projectId: string; onClose: () => void }) {
  const create = useCreateSite(projectId);
  const [key, setKey] = useState("");
  const [keyTouched, setKeyTouched] = useState(false);
  const [name, setName] = useState("");
  const [rootDirectory, setRootDirectory] = useState("");
  const error = create.error instanceof ApiError ? create.error : null;

  function slugify(value: string) {
    return value.toLowerCase().replace(/[^a-z0-9-]/g, "-").replace(/-+/g, "-").slice(0, 36) || "site";
  }

  async function onSubmit(e: FormEvent) {
    e.preventDefault();
    await create.mutateAsync({ key: key || slugify(name), name, rootDirectory: rootDirectory.trim() || undefined });
    onClose();
  }

  const urlPreview = `${key || "key"}.${projectId}.sites.<your domain>`;

  return (
    <Modal title="Create site" onClose={onClose} size="lg">
      <div className="flex flex-col gap-6 sm:flex-row">
        <form onSubmit={(e) => void onSubmit(e)} className="min-w-0 flex-1 space-y-4">
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
              placeholder="Marketing site"
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
              placeholder="marketing-site"
            />
            <span className="mt-1 block text-[11px] text-ink-500">Part of the site's public URL: {urlPreview}</span>
          </Field>
          <Field label="Root directory (optional)" error={error?.fieldErrors("rootDirectory")[0]}>
            <input
              className="input-base font-mono text-xs"
              value={rootDirectory}
              onChange={(e) => setRootDirectory(e.target.value)}
              placeholder="Leave blank if next.config.js is at the tar's root"
            />
          </Field>
          <div>
            <span className="mb-1.5 block text-xs font-medium uppercase tracking-wide text-ink-400">
              Your app must set
            </span>
            <pre className="overflow-x-auto rounded-lg border border-ink-700 bg-ink-950 px-3 py-2.5 font-mono text-xs text-ink-300">
              {`// next.config.js\nmodule.exports = { output: "standalone" };`}
            </pre>
            <span className="mt-1 block text-[11px] text-ink-500">
              Required — the build fails with a clear message if this is missing.
            </span>
          </div>
          <button type="submit" className="btn-primary w-full" disabled={create.isPending}>
            {create.isPending ? <Spinner /> : "Create site"}
          </button>
        </form>

        <aside className="w-full shrink-0 space-y-4 rounded-lg border border-ink-800 bg-ink-950 p-4 sm:w-52">
          <span className="block text-xs font-medium uppercase tracking-wide text-ink-500">Preview</span>
          <div>
            <p className="truncate text-sm font-medium text-ink-100">{name.trim() || "Untitled site"}</p>
            <div className="mt-2">
              <Badge tone="ink">Next.js</Badge>
            </div>
          </div>
          <div>
            <span className="block text-[11px] uppercase tracking-wide text-ink-600">Public URL</span>
            <p className="mt-1 break-all font-mono text-xs text-iris-300">{urlPreview}</p>
          </div>
        </aside>
      </div>
    </Modal>
  );
}
