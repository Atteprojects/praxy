import { Link, useParams } from "@tanstack/react-router";
import { useState, type FormEvent } from "react";
import { siteScreenshotUrl, useCreateSite, useSites } from "../api/sites";
import type { PraxySite } from "../api/types";
import { ApiError } from "../api/client";
import { Badge, ErrorNote, Field, FullPageSpinner, Modal, PageHeader, Spinner, timeAgo } from "../components/ui";

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
        <div className="surface flex flex-col items-center gap-4 px-6 py-16 text-center">
          <p className="max-w-sm text-sm text-ink-400">
            No sites yet. Create one, then deploy a Next.js app to it from its Deployments tab.
          </p>
          <button type="button" className="btn-primary" onClick={() => setCreating(true)}>
            + Create site
          </button>
        </div>
      ) : (
        <div className="flex flex-col gap-4">
          {sites.data.sites.map((site) => (
            <SiteCard key={site.id} projectId={projectId} site={site} />
          ))}
        </div>
      )}
    </div>
  );
}

function SiteCard({ projectId, site }: { projectId: string; site: PraxySite }) {
  const screenshotUrl = siteScreenshotUrl(projectId, site);
  const isLive = site.activeDeploymentId !== null && site.isRunning;

  return (
    <div className="surface flex flex-col overflow-hidden transition-colors hover:border-ink-700 sm:flex-row">
      <Link
        to="/project/$projectId/sites/$siteId"
        params={{ projectId, siteId: site.id }}
        className="block aspect-video w-full shrink-0 overflow-hidden border-b border-ink-800 bg-ink-950 sm:aspect-auto sm:w-72 sm:border-r sm:border-b-0"
      >
        {screenshotUrl ? (
          <img src={screenshotUrl} alt="" loading="lazy" className="h-full w-full object-cover object-top" />
        ) : (
          <SitePreviewPlaceholder name={site.name} />
        )}
      </Link>

      <div className="flex min-w-0 flex-1 flex-col justify-center gap-2 p-4 sm:p-5">
        <div className="flex flex-wrap items-center gap-2">
          <Link
            to="/project/$projectId/sites/$siteId"
            params={{ projectId, siteId: site.id }}
            className="truncate text-sm font-medium text-ink-100 hover:text-iris-300"
          >
            {site.name}
          </Link>
          <Badge tone="ink">Next.js</Badge>
          {site.enabled ? <Badge tone="mint">enabled</Badge> : <Badge tone="ink">disabled</Badge>}
          {site.activeDeploymentId ? (
            site.isRunning ? <Badge tone="mint">live</Badge> : <Badge tone="amber">starting</Badge>
          ) : (
            <Badge tone="amber">no deployment</Badge>
          )}
        </div>

        <p className="truncate font-mono text-xs text-ink-500">{site.publicUrl}</p>

        <div className="mt-1 flex items-center justify-between gap-3 text-xs text-ink-500">
          <span>Updated {timeAgo(site.updatedAt)}</span>
          <div className="flex items-center gap-3">
            {isLive ? (
              <a href={site.publicUrl} target="_blank" rel="noreferrer" className="font-medium text-iris-300 hover:underline">
                Visit ↗
              </a>
            ) : null}
            <Link
              to="/project/$projectId/sites/$siteId"
              params={{ projectId, siteId: site.id }}
              className="font-medium text-ink-300 hover:text-ink-100"
            >
              Open
            </Link>
          </div>
        </div>
      </div>
    </div>
  );
}

/** No preview yet — no deployment, still building, or the capture just hasn't landed. Deliberately flat: no gradient, no glow, matching the rest of the console's understated language. */
function SitePreviewPlaceholder({ name }: { name: string }) {
  const initial = name.trim().charAt(0).toUpperCase() || "?";
  return (
    <div className="flex h-full w-full items-center justify-center">
      <span className="text-4xl font-semibold text-ink-800 select-none">{initial}</span>
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
      <div className="space-y-5">
        <div className="flex items-center gap-4 rounded-lg border border-ink-800 bg-ink-950 p-4">
          <div className="min-w-0 flex-1">
            <div className="flex items-center gap-2">
              <p className="truncate text-sm font-medium text-ink-100">{name.trim() || "Untitled site"}</p>
              <Badge tone="ink">Next.js</Badge>
            </div>
            <p className="mt-1.5 break-words font-mono text-xs text-iris-300">{withBreakOpportunities(urlPreview)}</p>
          </div>
        </div>

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
      </div>
    </Modal>
  );
}

/**
 * Inserts a zero-width space after each "." so a long dot-separated string (a URL with no spaces)
 * wraps at those natural boundaries instead of `overflow-wrap`'s default fallback of breaking
 * mid-word wherever it runs out of room — which, for a 32-char hex project id, means mid-hex-digit.
 */
function withBreakOpportunities(value: string): string {
  return value.replaceAll(".", ".​");
}
