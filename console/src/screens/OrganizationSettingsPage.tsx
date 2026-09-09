import { useNavigate, useParams } from "@tanstack/react-router";
import { useState } from "react";
import { ApiError } from "../api/client";
import { wireId } from "../api/ids";
import {
  useDeleteOrganization,
  useOrganization,
  useOrganizations,
  useProjects,
  useUpdateOrganization,
} from "../api/queries";
import { useToast } from "../components/toast";
import { ErrorNote, FullPageSpinner, Spinner } from "../components/ui";
import { OrganizationTabs } from "./OrganizationTabs";

/**
 * The organization's Settings tab. Renaming lives in the header's inline title (shared by all three
 * tabs), so what is left here is the danger zone — deliberately its own tab rather than trailing
 * the project grid, where it sat before and where an operator scrolling to the end of their
 * projects met a delete control they weren't looking for.
 */
export function OrganizationSettingsPage() {
  const organizationId = wireId(
    (useParams({ strict: false }) as { organizationId: string }).organizationId,
  );
  const organization = useOrganization(organizationId);
  const organizations = useOrganizations();
  const projects = useProjects();
  const update = useUpdateOrganization(organizationId);

  if (organization.isPending || organizations.isPending || projects.isPending) return <FullPageSpinner />;
  if (organization.isError) throw organization.error;
  if (organizations.isError) throw organizations.error;
  if (projects.isError) throw projects.error;

  const isOwner = organization.data.role === "owner";
  const owned = projects.data.projects.filter((project) => project.organizationId === organizationId);

  return (
    <>
      <OrganizationTabs
        organizationId={organizationId}
        name={organization.data.name}
        active="settings"
        isOwner={isOwner}
        onRename={(name) => update.mutateAsync({ name })}
        description={isOwner ? "Rename this organization from its title above." : undefined}
      />

      {isOwner ? (
        <OrganizationDangerZone
          organizationId={organizationId}
          organizationName={organization.data.name}
          hasProjects={owned.length > 0}
          isOnlyOrganization={organizations.data.total <= 1}
        />
      ) : (
        <p className="surface p-6 text-sm text-ink-400">
          Only an owner can rename or delete this organization.
        </p>
      )}
    </>
  );
}

/**
 * Same typed-name-confirmation shape as `ProjectOverviewPage`'s danger zone, but with two states
 * that never reach a confirm form at all: the server refuses both outright (no `force`, no
 * override), so the UI explains why up front rather than letting the operator type a name into a
 * button that's always going to 409.
 */
function OrganizationDangerZone({
  organizationId,
  organizationName,
  hasProjects,
  isOnlyOrganization,
}: {
  organizationId: string;
  organizationName: string;
  hasProjects: boolean;
  isOnlyOrganization: boolean;
}) {
  const navigate = useNavigate();
  const remove = useDeleteOrganization();
  const toast = useToast();
  const [confirmName, setConfirmName] = useState("");
  const error = remove.error instanceof ApiError ? remove.error : null;

  async function onDelete() {
    await remove.mutateAsync(organizationId);
    await navigate({ to: "/" });
    toast.success(`Deleted "${organizationName}".`);
  }

  return (
    <div className="max-w-3xl surface border-coral-400/20 p-5">
      <h2 className="mb-3 text-sm font-medium text-coral-400">Danger zone</h2>
      {isOnlyOrganization ? (
        <p className="text-xs text-ink-500">
          This is your only organization — create another before this one can be deleted.
        </p>
      ) : hasProjects ? (
        <p className="text-xs text-ink-500">
          Delete every project in <span className="font-mono text-ink-300">{organizationName}</span> before this
          organization can be deleted.
        </p>
      ) : (
        <>
          <p className="mb-3 text-xs text-ink-500">
            Deleting <span className="font-mono text-ink-300">{organizationName}</span> cannot be undone.
          </p>
          {error ? <div className="mb-3"><ErrorNote message={error.message} /></div> : null}
          <p className="mb-2 text-xs text-ink-500">
            Type <span className="font-mono text-ink-300">{organizationName}</span> to confirm.
          </p>
          <div className="flex gap-2">
            <input
              className="input-base flex-1"
              value={confirmName}
              onChange={(e) => setConfirmName(e.target.value)}
              placeholder={organizationName}
            />
            <button
              type="button"
              className="btn-ghost shrink-0 border border-coral-400/60 text-coral-400 disabled:opacity-40"
              disabled={confirmName !== organizationName || remove.isPending}
              onClick={() => void onDelete()}
            >
              {remove.isPending ? <Spinner /> : "Delete organization"}
            </button>
          </div>
        </>
      )}
    </div>
  );
}
