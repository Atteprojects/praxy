import { Link } from "@tanstack/react-router";
import type { ReactNode } from "react";
import { IdChip, PageHeader } from "../components/ui";

/**
 * The organization's own header and section tabs — same shape as `AuthTabs`/`MessagingTabs`, and
 * for the same reason those exist: sibling areas with no single parent record to hang a detail
 * header off.
 *
 * The title is a plain heading, deliberately: renaming lives on the Settings tab rather than as an
 * inline-editable title. This header renders on all three tabs, so an inline control put a mutation
 * on Projects and Members — screens that are otherwise read-only — where a stray click could start
 * an edit nobody asked for. Settings is where destructive and configuration actions already live.
 */
export function OrganizationTabs({
  organizationId,
  name,
  active,
  description,
  actions,
}: {
  organizationId: string;
  name: string;
  active: "projects" | "members" | "settings";
  description?: ReactNode;
  actions?: ReactNode;
}) {
  return (
    <PageHeader
      title={<h1 className="text-2xl font-semibold tracking-tight">{name}</h1>}
      chips={<IdChip id={organizationId} />}
      description={description}
      actions={actions}
      tabs={
        <div className="flex gap-1 border-b border-ink-800" role="tablist">
          <TabLink
            to="/organization/$organizationId"
            label="Projects"
            active={active === "projects"}
            organizationId={organizationId}
          />
          <TabLink
            to="/organization/$organizationId/members"
            label="Members"
            active={active === "members"}
            organizationId={organizationId}
          />
          <TabLink
            to="/organization/$organizationId/settings"
            label="Settings"
            active={active === "settings"}
            organizationId={organizationId}
          />
        </div>
      }
    />
  );
}

function TabLink({
  to,
  label,
  active,
  organizationId,
}: {
  to: string;
  label: string;
  active: boolean;
  organizationId: string;
}) {
  const className = `-mb-px border-b-2 px-3 py-2 text-sm font-medium transition-colors ${
    active ? "border-iris-400 text-ink-100" : "border-transparent text-ink-500 hover:text-ink-300"
  }`;
  return (
    <Link to={to} params={{ organizationId }} className={className} role="tab" aria-selected={active}>
      {label}
    </Link>
  );
}
