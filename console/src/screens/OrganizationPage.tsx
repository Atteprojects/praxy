import { Link, Navigate, useNavigate, useParams } from "@tanstack/react-router";
import { useEffect, useState, type FormEvent } from "react";
import { ApiError } from "../api/client";
import { wireId } from "../api/ids";
import {
  useCreateOrganization,
  useDeleteOrganization,
  useOrganization,
  useOrganizations,
  useProjects,
  useUpdateOrganization,
} from "../api/queries";
import type { Organization } from "../api/types";
import { useToast } from "../components/toast";
import {
  Badge,
  ErrorNote,
  Field,
  FullPageSpinner,
  IdChip,
  InlineEditableTitle,
  Modal,
  PageHeader,
  Spinner,
} from "../components/ui";
import { STR } from "../strings";
import { CreateProjectCard } from "./CreateProjectCard";

/** The org id is not on the session, so switching is remembered client-side, per browser. */
const LAST_ORGANIZATION_KEY = "praxy.lastOrganizationId";

function rememberOrganization(organizationId: string) {
  try {
    localStorage.setItem(LAST_ORGANIZATION_KEY, organizationId);
  } catch {
    // Private browsing / storage disabled: switching still works, it just won't be remembered.
  }
}

function lastRememberedOrganization(): string | null {
  try {
    return localStorage.getItem(LAST_ORGANIZATION_KEY);
  } catch {
    return null;
  }
}

/**
 * The post-login landing route. The org id is not on the session, so the console has to resolve
 * one before it can build the URL: the remembered last-used org if it's still one of the
 * operator's own, else the operator's only org, else — now that there can be more than one with
 * nothing remembered — a picker. That resolution is a spinner, never a glimpse of a project list
 * at a bare "/": rendering here and then jumping would flash a screen the user never asked for.
 *
 * "/" stays the canonical entry point — the login redirects, the logo and every "back to projects"
 * link still point at it, and bookmarks keep working — it just forwards to the resolved org.
 */
export function HomeRedirect() {
  const organizations = useOrganizations();

  if (organizations.isPending) return <FullPageSpinner />;
  if (organizations.isError) throw organizations.error;

  const list = organizations.data.organizations;
  // Unreachable in Phase 1 (every operator starts with exactly one, and none could be deleted
  // down to zero) — Phase 2's own "leave an organization" is deliberately allowed even when it's
  // an operator's last one (docs/handoff/organizations-phase-2-prompt.md's owner-test walks
  // through exactly that), so this is now a real state to land in, not just a broken claim.
  if (list.length === 0) return <NoOrganizationsCard />;

  const remembered = lastRememberedOrganization();
  const target = list.find((o) => o.id === remembered) ?? (list.length === 1 ? list[0] : undefined);

  if (target) return <Navigate to="/organization/$organizationId" params={{ organizationId: target.id }} replace />;

  return <OrganizationPicker organizations={list} />;
}

/**
 * Reachable only by leaving your last organization (Phase 2) — every other path into "zero
 * organizations" was already impossible before this phase. The account itself is fine; it just
 * needs somewhere to land, so this is the same create form as `CreateOrganizationModal`, without
 * the overlay chrome there's nothing behind to dim.
 */
function NoOrganizationsCard() {
  const create = useCreateOrganization();
  const navigate = useNavigate();
  const [name, setName] = useState("");
  const error = create.error instanceof ApiError ? create.error : null;

  async function onSubmit(e: FormEvent) {
    e.preventDefault();
    const organization = await create.mutateAsync({ name });
    await navigate({ to: "/organization/$organizationId", params: { organizationId: organization.id } });
  }

  return (
    <div className="mx-auto w-full max-w-sm px-6 py-16">
      <h1 className="mb-2 text-center text-lg font-semibold">Create an organization</h1>
      <p className="mb-6 text-center text-sm text-ink-400">
        You don't belong to one right now — create one to continue.
      </p>
      <form onSubmit={(e) => void onSubmit(e)} className="space-y-4">
        {error && !error.envelope.fields ? <ErrorNote message={error.message} /> : null}
        <Field label="Name" error={error?.fieldErrors("name")[0]}>
          <input
            className="input-base"
            required
            autoFocus
            value={name}
            onChange={(e) => setName(e.target.value)}
            placeholder="Acme Inc."
          />
        </Field>
        <button type="submit" className="btn-primary w-full" disabled={create.isPending}>
          {create.isPending ? <Spinner /> : "Create organization"}
        </button>
      </form>
    </div>
  );
}

function OrganizationPicker({ organizations }: { organizations: Organization[] }) {
  return (
    <div className="mx-auto w-full max-w-md px-6 py-16">
      <h1 className="mb-6 text-center text-lg font-semibold">Choose an organization</h1>
      <div className="space-y-2">
        {organizations.map((organization) => (
          <Link
            key={organization.id}
            to="/organization/$organizationId"
            params={{ organizationId: organization.id }}
            className="surface flex items-center justify-between gap-3 p-4 transition-colors hover:border-iris-500/60"
          >
            <span className="truncate font-medium">{organization.name}</span>
            <IdChip id={organization.id} />
          </Link>
        ))}
      </div>
    </div>
  );
}

/** The projects list, rendered as its owning organization's page: name on top, id in the URL. */
export function OrganizationPage() {
  // A route param, not a parsed API response — a real trust boundary, so it's branded explicitly.
  const organizationId = wireId(
    (useParams({ strict: false }) as { organizationId: string }).organizationId,
  );
  const organization = useOrganization(organizationId);
  const organizations = useOrganizations();
  const projects = useProjects();
  const update = useUpdateOrganization(organizationId);
  const navigate = useNavigate();
  const [creatingProject, setCreatingProject] = useState(false);
  const [creatingOrg, setCreatingOrg] = useState(false);

  // Every route into an org page — the picker, the switcher, a direct link — passes through here,
  // so remembering it in one place covers all of them.
  useEffect(() => {
    rememberOrganization(organizationId);
  }, [organizationId]);

  if (organization.isPending || organizations.isPending || projects.isPending) return <FullPageSpinner />;
  if (organization.isError) throw organization.error;
  if (organizations.isError) throw organizations.error;
  if (projects.isError) throw projects.error;

  const owned = projects.data.projects.filter((project) => project.organizationId === organizationId);
  // Cosmetic only — the 409s (last org, has projects) and the 403 an owner-only mutation returns
  // are the actual gate either way, matching the danger zone's own convention of explaining rather
  // than just disabling.
  const isOwner = organization.data.role === "owner";

  // Genuinely fresh instance — one org, zero projects anywhere: no chrome, just the create card,
  // the Appwrite onboarding pattern minus the org ceremony. Once a second org exists, or any
  // project exists anywhere, an empty *this* org is a deliberate state (about to be renamed,
  // switched away from, or deleted), not a first run, so it keeps its full chrome below instead.
  if (organizations.data.total === 1 && projects.data.total === 0)
    return <CreateProjectCard standalone organizationId={organizationId} />;

  return (
    <div className="mx-auto w-full max-w-5xl px-6 py-10">
      <PageHeader
        title={
          isOwner ? (
            <InlineEditableTitle value={organization.data.name} onSave={(name) => update.mutateAsync({ name })} />
          ) : (
            <h1 className="text-2xl font-semibold tracking-tight">{organization.data.name}</h1>
          )
        }
        chips={<IdChip id={organization.data.id} />}
        description={`${STR.projects} in this ${STR.organization}.`}
        actions={
          <>
            {organizations.data.total > 1 ? (
              <select
                aria-label="Switch organization"
                className="input-base"
                value={organizationId}
                onChange={(e) =>
                  void navigate({
                    to: "/organization/$organizationId",
                    params: { organizationId: e.target.value },
                  })
                }
              >
                {organizations.data.organizations.map((o) => (
                  <option key={o.id} value={o.id}>
                    {o.name}
                  </option>
                ))}
              </select>
            ) : null}
            <Link
              to="/organization/$organizationId/members"
              params={{ organizationId }}
              className="btn-ghost border border-ink-700"
            >
              Members
            </Link>
            <button
              type="button"
              className="btn-ghost border border-ink-700"
              onClick={() => setCreatingOrg(true)}
            >
              + New organization
            </button>
            <button type="button" onClick={() => setCreatingProject(true)} className="btn-primary">
              + Create project
            </button>
          </>
        }
      />

      {creatingOrg ? <CreateOrganizationModal onClose={() => setCreatingOrg(false)} /> : null}

      {creatingProject ? (
        <div
          className="fixed inset-0 z-40 grid place-items-center bg-ink-950/70 p-4 backdrop-blur-sm"
          onClick={(e) => e.target === e.currentTarget && setCreatingProject(false)}
        >
          <CreateProjectCard organizationId={organizationId} />
        </div>
      ) : null}

      {owned.length === 0 ? (
        <p className="surface p-6 text-sm text-ink-400">No projects in this organization yet.</p>
      ) : (
        <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
          {owned.map((project) => (
            <Link
              key={project.id}
              to="/project/$projectId"
              params={{ projectId: project.id }}
              className="surface group flex flex-col p-6 transition-colors hover:border-iris-500/60"
            >
              <div className="mb-4 flex items-start justify-between gap-3">
                <span className="truncate text-lg font-semibold group-hover:text-white">{project.name}</span>
                <Badge tone={project.lastPingAt ? "mint" : "ink"}>
                  {project.lastPingAt ? "Connected" : "Waiting"}
                </Badge>
              </div>
              <div onClick={(e) => e.preventDefault()}>
                <IdChip id={project.id} />
              </div>
              <p className="mt-auto pt-6 text-xs text-ink-500">
                Created {new Date(project.createdAt).toLocaleDateString()}
              </p>
            </Link>
          ))}
        </div>
      )}

      {isOwner ? (
        <OrganizationDangerZone
          organizationId={organizationId}
          organizationName={organization.data.name}
          hasProjects={owned.length > 0}
          isOnlyOrganization={organizations.data.total <= 1}
        />
      ) : null}
    </div>
  );
}

function CreateOrganizationModal({ onClose }: { onClose: () => void }) {
  const create = useCreateOrganization();
  const navigate = useNavigate();
  const [name, setName] = useState("");
  const error = create.error instanceof ApiError ? create.error : null;

  async function onSubmit(e: FormEvent) {
    e.preventDefault();
    const organization = await create.mutateAsync({ name });
    onClose();
    await navigate({ to: "/organization/$organizationId", params: { organizationId: organization.id } });
  }

  return (
    <Modal title="New organization" onClose={onClose}>
      <form onSubmit={(e) => void onSubmit(e)} className="space-y-4">
        {error && !error.envelope.fields ? <ErrorNote message={error.message} /> : null}
        <Field label="Name" error={error?.fieldErrors("name")[0]}>
          <input
            className="input-base"
            required
            autoFocus
            value={name}
            onChange={(e) => setName(e.target.value)}
            placeholder="Acme Inc."
          />
        </Field>
        <button type="submit" className="btn-primary w-full" disabled={create.isPending}>
          {create.isPending ? <Spinner /> : "Create organization"}
        </button>
      </form>
    </Modal>
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
    <div className="mt-8 max-w-3xl surface border-coral-400/20 p-5">
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
