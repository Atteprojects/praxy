import { Link } from "@tanstack/react-router";
import type { ReactNode } from "react";
import { PageHeader } from "../components/ui";

/** Shared top-tab header for Auth's three sibling areas — same shape as MessagingTabs, for the same reason: no single parent record to hang tabs off. */
export function AuthTabs({
  projectId,
  active,
  description,
  actions,
}: {
  projectId: string;
  active: "users" | "teams" | "settings";
  /** Sub-area blurb — rendered under the title so it can't drift into the action row. */
  description?: ReactNode;
  actions?: ReactNode;
}) {
  return (
    <PageHeader
      title="Auth"
      description={description}
      actions={actions}
      tabs={
        <div className="flex gap-1 border-b border-ink-800" role="tablist">
          <TabLink to="/project/$projectId/auth/users" label="Users" active={active === "users"} projectId={projectId} />
          <TabLink to="/project/$projectId/auth/teams" label="Teams" active={active === "teams"} projectId={projectId} />
          <TabLink to="/project/$projectId/auth/settings" label="Settings" active={active === "settings"} projectId={projectId} />
        </div>
      }
    />
  );
}

function TabLink({
  to,
  label,
  active,
  projectId,
}: {
  to: string;
  label: string;
  active: boolean;
  projectId: string;
}) {
  const className = `-mb-px border-b-2 px-3 py-2 text-sm font-medium transition-colors ${
    active ? "border-iris-400 text-ink-100" : "border-transparent text-ink-500 hover:text-ink-300"
  }`;
  return (
    <Link to={to} params={{ projectId }} className={className} role="tab" aria-selected={active}>
      {label}
    </Link>
  );
}
