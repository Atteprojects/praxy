import { Link } from "@tanstack/react-router";
import type { ReactNode } from "react";
import { IdChip, InlineEditableTitle, PageHeader } from "../components/ui";

/**
 * The organization's own header and section tabs — same shape as `AuthTabs`/`MessagingTabs`, and
 * for the same reason those exist: sibling areas with no single parent record to hang a detail
 * header off. Unlike those two, the title here *is* a record, so it doubles as the rename control
 * for an owner (a plain heading otherwise — the 403 is the real gate, this is cosmetic).
 */
export function OrganizationTabs({
  organizationId,
  name,
  active,
  isOwner,
  onRename,
  description,
  actions,
}: {
  organizationId: string;
  name: string;
  active: "projects" | "members" | "settings";
  isOwner: boolean;
  onRename: (name: string) => Promise<unknown>;
  description?: ReactNode;
  actions?: ReactNode;
}) {
  return (
    <PageHeader
      title={
        isOwner ? (
          <InlineEditableTitle value={name} onSave={(next) => onRename(next)} />
        ) : (
          <h1 className="text-2xl font-semibold tracking-tight">{name}</h1>
        )
      }
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
