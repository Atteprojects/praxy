import { useNavigate, useParams } from "@tanstack/react-router";
import { useEffect, useState, type FormEvent } from "react";
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
import { ErrorNote, Field, FullPageSpinner, Spinner } from "../components/ui";
import { OrganizationTabs } from "./OrganizationTabs";

/**
 * The organization's Settings tab: rename, and the danger zone.
 *
 * Both live here rather than anywhere else for the same reason. The danger zone used to trail the
 * project grid, where an operator scrolling to the end of their projects met a delete control they
 * weren't looking for; renaming used to be an inline-editable page title, which put a mutation on
 * Projects and Members too, since that header renders on all three tabs.
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
      />

      {isOwner ? (
        <div className="space-y-6">
          <RenameOrganization
            currentName={organization.data.name}
            onRename={(name) => update.mutateAsync({ name })}
          />
          <OrganizationDangerZone
            organizationId={organizationId}
            organizationName={organization.data.name}
            hasProjects={owned.length > 0}
            isOnlyOrganization={organizations.data.total <= 1}
          />
        </div>
      ) : (
        <p className="surface p-6 text-sm text-ink-400">
          Only an owner can rename or delete this organization.
        </p>
      )}
    </>
  );
}

/**
 * `currentName` is the server's value, so the field resyncs when a rename lands (or when another
 * tab's rename arrives through the query cache) rather than holding whatever was last typed. Save
 * stays disabled until the name actually differs, so the button can't fire a no-op PATCH.
 */
function RenameOrganization({
  currentName,
  onRename,
}: {
  currentName: string;
  onRename: (name: string) => Promise<unknown>;
}) {
  const [name, setName] = useState(currentName);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<ApiError | null>(null);

  useEffect(() => {
    setName(currentName);
  }, [currentName]);

  async function onSubmit(e: FormEvent) {
    e.preventDefault();
    setError(null);
    setSaving(true);
    try {
      await onRename(name.trim());
    } catch (err) {
      if (err instanceof ApiError) setError(err);
      else throw err;
    } finally {
      setSaving(false);
    }
  }

  const unchanged = name.trim() === currentName || name.trim().length === 0;

  return (
    <form onSubmit={(e) => void onSubmit(e)} className="max-w-3xl surface p-5">
      <h2 className="mb-3 text-sm font-medium text-ink-100">Name</h2>
      {error && !error.envelope.fields ? (
        <div className="mb-3"><ErrorNote message={error.message} /></div>
      ) : null}
      <div className="flex items-end gap-2">
        <div className="min-w-0 flex-1">
          <Field label="Organization name" error={error?.fieldErrors("name")[0]}>
            <input
              className="input-base"
              value={name}
              onChange={(e) => setName(e.target.value)}
              placeholder={currentName}
            />
          </Field>
        </div>
        <button type="submit" className="btn-primary shrink-0" disabled={unchanged || saving}>
          {saving ? <Spinner /> : "Save"}
        </button>
      </div>
    </form>
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
