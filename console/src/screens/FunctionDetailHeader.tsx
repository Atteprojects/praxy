import { Link } from "@tanstack/react-router";
import type { PraxyFunction } from "../api/types";
import { Badge, IdChip } from "../components/ui";

/** Shared by Deployments/Executions/Settings: name + status chips + the tab row between the three function sub-views — same shape as TableDetailHeader. */
export function FunctionDetailHeader({
  projectId,
  fn,
  active,
}: {
  projectId: string;
  fn: PraxyFunction;
  active: "deployments" | "executions" | "settings";
}) {
  return (
    <div className="mb-6">
      <div className="mb-4 flex flex-wrap items-center gap-3">
        <h1 className="text-2xl font-semibold tracking-tight">{fn.name}</h1>
        <IdChip id={fn.id} />
        <Badge tone="ink">{fn.runtime}</Badge>
        {!fn.enabled ? <Badge tone="ink">disabled</Badge> : null}
        {fn.activeDeploymentId ? (
          fn.isWarm ? <Badge tone="iris">warm</Badge> : <Badge tone="ink">cold</Badge>
        ) : (
          <Badge tone="amber">no active deployment</Badge>
        )}
      </div>
      <div className="flex gap-1 border-b border-ink-800" role="tablist">
        <TabLink to="deployments" label="Deployments" active={active === "deployments"} projectId={projectId} functionId={fn.id} />
        <TabLink to="executions" label="Executions" active={active === "executions"} projectId={projectId} functionId={fn.id} />
        <TabLink to="settings" label="Settings" active={active === "settings"} projectId={projectId} functionId={fn.id} />
      </div>
    </div>
  );
}

const TAB_ROUTES = {
  deployments: "/project/$projectId/functions/$functionId",
  executions: "/project/$projectId/functions/$functionId/executions",
  settings: "/project/$projectId/functions/$functionId/settings",
} as const;

function TabLink({
  to,
  label,
  active,
  projectId,
  functionId,
}: {
  to: keyof typeof TAB_ROUTES;
  label: string;
  active: boolean;
  projectId: string;
  functionId: string;
}) {
  const className = `-mb-px border-b-2 px-3 py-2 text-sm font-medium transition-colors ${
    active ? "border-iris-400 text-ink-100" : "border-transparent text-ink-500 hover:text-ink-300"
  }`;
  return (
    <Link
      to={TAB_ROUTES[to]}
      params={{ projectId, functionId }}
      activeOptions={{ exact: to === "deployments" }}
      className={className}
      role="tab"
      aria-selected={active}
    >
      {label}
    </Link>
  );
}
