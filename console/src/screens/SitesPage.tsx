import { Link, useNavigate, useParams } from "@tanstack/react-router";
import { useState, type FormEvent } from "react";
import { useCreateSite, useDeploySiteStarterTemplate, useSites } from "../api/sites";
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
          <div className="grid size-12 place-items-center rounded-full border border-ink-800 bg-ink-900 text-ink-500">
            <NextjsGlyph className="size-6" />
          </div>
          <div>
            <p className="text-sm font-medium text-ink-100">No sites yet</p>
            <p className="mt-1 max-w-sm text-sm text-ink-500">
              Create one, then deploy the starter template or your own Next.js app to it.
            </p>
          </div>
          <button type="button" className="btn-primary" onClick={() => setCreating(true)}>
            + Create site
          </button>
        </div>
      ) : (
        <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 xl:grid-cols-3">
          {sites.data.sites.map((site) => (
            <SiteCard key={site.id} projectId={projectId} site={site} />
          ))}
        </div>
      )}
    </div>
  );
}

function SiteCard({ projectId, site }: { projectId: string; site: PraxySite }) {
  // `!= null`, not `!== null`: a site with no active deployment omits activeDeploymentId from
  // the JSON, so it arrives undefined and a strict check would pass for every site.
  const isLive = site.activeDeploymentId != null && site.isRunning;

  return (
    <Link
      to="/project/$projectId/sites/$siteId"
      params={{ projectId, siteId: site.id }}
      className="surface group flex flex-col overflow-hidden transition-colors hover:border-ink-600"
    >
      <div className="relative flex aspect-video items-center justify-center overflow-hidden border-b border-ink-800 bg-[radial-gradient(circle_at_30%_20%,theme(colors.iris.500/0.12),transparent_60%)]">
        <NextjsGlyph className="size-10 text-ink-700 transition-colors group-hover:text-ink-600" />
        <span className="absolute bottom-2.5 left-2.5">
          <Badge tone="ink">Next.js</Badge>
        </span>
      </div>

      <div className="flex flex-1 flex-col gap-2 p-4">
        <div className="flex items-start justify-between gap-2">
          <p className="truncate text-sm font-medium text-ink-100 group-hover:text-iris-300">{site.name}</p>
          {isLive ? <Badge tone="mint">live</Badge> : site.activeDeploymentId ? (
            <Badge tone="amber">starting</Badge>
          ) : (
            <Badge tone="amber">no deployment</Badge>
          )}
        </div>
        <p className="truncate font-mono text-xs text-ink-500">{site.publicUrl}</p>
        <div className="mt-auto flex items-center justify-between gap-3 pt-2 text-xs text-ink-500">
          <span>Updated {timeAgo(site.updatedAt)}</span>
          {!site.enabled ? <Badge tone="ink">disabled</Badge> : null}
        </div>
      </div>
    </Link>
  );
}

/** A flat, monochrome Next.js "N" mark — decoration only, styled entirely via `currentColor` so it always matches its container's tone. */
function NextjsGlyph({ className }: { className?: string }) {
  return (
    <svg viewBox="0 0 24 24" fill="none" className={className} aria-hidden>
      <circle cx="12" cy="12" r="11" stroke="currentColor" strokeWidth="1.5" />
      <path d="M8 8v8M8 8l8 8M16 8v5" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" />
    </svg>
  );
}

function CreateSiteModal({ projectId, onClose }: { projectId: string; onClose: () => void }) {
  const create = useCreateSite(projectId);
  const deployTemplate = useDeploySiteStarterTemplate(projectId);
  const navigate = useNavigate();
  const [start, setStart] = useState<"blank" | "template">("template");
  const [key, setKey] = useState("");
  const [keyTouched, setKeyTouched] = useState(false);
  const [name, setName] = useState("");
  const [rootDirectory, setRootDirectory] = useState("");
  const error = create.error instanceof ApiError ? create.error : null;
  const isPending = create.isPending || deployTemplate.isPending;

  function slugify(value: string) {
    return value.toLowerCase().replace(/[^a-z0-9-]/g, "-").replace(/-+/g, "-").slice(0, 36) || "site";
  }

  async function onSubmit(e: FormEvent) {
    e.preventDefault();
    const site = await create.mutateAsync({ key: key || slugify(name), name, rootDirectory: rootDirectory.trim() || undefined });
    if (start === "template") {
      await deployTemplate.mutateAsync(site.id);
      onClose();
      void navigate({ to: "/project/$projectId/sites/$siteId", params: { projectId, siteId: site.id } });
      return;
    }
    onClose();
  }

  const urlPreview = `${key || "key"}.${projectId}.sites.<your domain>`;

  return (
    <Modal title="Create site" onClose={onClose} size="lg">
      <div className="space-y-5">
        <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
          <StartOption
            active={start === "template"}
            title="Starter template"
            description="Deploy now with a bundled Next.js app — see a real site live in seconds."
            onClick={() => setStart("template")}
          />
          <StartOption
            active={start === "blank"}
            title="Blank"
            description="Create the site now, upload your own app's tar afterward."
            onClick={() => setStart("blank")}
          />
        </div>

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
          {start === "blank" ? (
            <>
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
            </>
          ) : null}
          <button type="submit" className="btn-primary w-full" disabled={isPending}>
            {isPending ? <Spinner /> : start === "template" ? "Create & deploy starter template" : "Create site"}
          </button>
        </form>
      </div>
    </Modal>
  );
}

function StartOption({
  active,
  title,
  description,
  onClick,
}: {
  active: boolean;
  title: string;
  description: string;
  onClick: () => void;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      className={`rounded-lg border p-3.5 text-left transition-colors ${
        active ? "border-iris-500 bg-iris-500/5" : "border-ink-800 bg-ink-900 hover:border-ink-700"
      }`}
    >
      <span className={`block text-sm font-medium ${active ? "text-iris-300" : "text-ink-100"}`}>{title}</span>
      <span className="mt-0.5 block text-xs text-ink-500">{description}</span>
    </button>
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
