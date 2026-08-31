import { Link } from "@tanstack/react-router";
import type { PraxySite } from "../api/types";
import { Badge, IdChip } from "../components/ui";

/** Shared by Deployments/Settings: name + status chips + the tab row between the two site sub-views — same shape as FunctionDetailHeader. */
export function SiteDetailHeader({
  projectId,
  site,
  active,
}: {
  projectId: string;
  site: PraxySite;
  active: "deployments" | "logs" | "settings";
}) {
  return (
    <div className="mb-6">
      <div className="mb-2 flex flex-wrap items-center gap-3">
        <h1 className="text-2xl font-semibold tracking-tight">{site.name}</h1>
        <IdChip id={site.id} />
        {!site.enabled ? <Badge tone="ink">disabled</Badge> : null}
        {site.activeDeploymentId ? (
          site.isRunning ? <Badge tone="mint">live</Badge> : <Badge tone="amber">starting</Badge>
        ) : (
          <Badge tone="amber">no active deployment</Badge>
        )}
      </div>
      {site.activeDeploymentId && site.isRunning ? (
        <a
          href={site.publicUrl}
          target="_blank"
          rel="noreferrer"
          className="mb-4 block truncate text-sm text-iris-300 hover:text-iris-200 hover:underline"
        >
          {site.publicUrl}
        </a>
      ) : (
        <p className="mb-4 truncate font-mono text-sm text-ink-500">{site.publicUrl}</p>
      )}
      <div className="flex gap-1 border-b border-ink-800" role="tablist">
        <TabLink to="deployments" label="Deployments" active={active === "deployments"} projectId={projectId} siteId={site.id} />
        <TabLink to="logs" label="Logs" active={active === "logs"} projectId={projectId} siteId={site.id} />
        <TabLink to="settings" label="Settings" active={active === "settings"} projectId={projectId} siteId={site.id} />
      </div>
    </div>
  );
}

const TAB_ROUTES = {
  deployments: "/project/$projectId/sites/$siteId",
  logs: "/project/$projectId/sites/$siteId/logs",
  settings: "/project/$projectId/sites/$siteId/settings",
} as const;

function TabLink({
  to,
  label,
  active,
  projectId,
  siteId,
}: {
  to: keyof typeof TAB_ROUTES;
  label: string;
  active: boolean;
  projectId: string;
  siteId: string;
}) {
  const className = `-mb-px border-b-2 px-3 py-2 text-sm font-medium transition-colors ${
    active ? "border-iris-400 text-ink-100" : "border-transparent text-ink-500 hover:text-ink-300"
  }`;
  return (
    <Link
      to={TAB_ROUTES[to]}
      params={{ projectId, siteId }}
      activeOptions={{ exact: to === "deployments" }}
      className={className}
      role="tab"
      aria-selected={active}
    >
      {label}
    </Link>
  );
}
