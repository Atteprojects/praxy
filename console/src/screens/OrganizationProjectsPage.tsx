import { Link, useParams } from "@tanstack/react-router";
import { useState } from "react";
import { wireId } from "../api/ids";
import { useOrganization, useOrganizations, useProjects } from "../api/queries";
import { Badge, FullPageSpinner, IdChip } from "../components/ui";
import { CreateProjectCard } from "./CreateProjectCard";
import { OrganizationTabs } from "./OrganizationTabs";

/**
 * The organization's default tab: the projects it owns.
 *
 * `GET /v1/console/projects` returns every project the operator can reach across every
 * organization, with `organizationId` on each, and there is no server-side filter on purpose — the
 * switcher rail needs the full list anyway, so filtering here costs one pass over an array the
 * console already has, while a filtered endpoint would cost a second request.
 */
export function OrganizationProjectsPage() {
  const organizationId = wireId(
    (useParams({ strict: false }) as { organizationId: string }).organizationId,
  );
  const organization = useOrganization(organizationId);
  const organizations = useOrganizations();
  const projects = useProjects();
  const [creating, setCreating] = useState(false);

  if (organization.isPending || organizations.isPending || projects.isPending) return <FullPageSpinner />;
  if (organization.isError) throw organization.error;
  if (organizations.isError) throw organizations.error;
  if (projects.isError) throw projects.error;

  const owned = projects.data.projects.filter((project) => project.organizationId === organizationId);

  // Genuinely fresh instance — one organization, zero projects anywhere: no chrome at all, just the
  // create card, the Appwrite onboarding pattern minus the organization ceremony. Once a second
  // organization exists, or any project exists anywhere, an empty *this* organization is a
  // deliberate state (about to be renamed, switched away from, or deleted) rather than a first run,
  // so it keeps the full tabbed chrome below.
  if (organizations.data.total === 1 && projects.data.total === 0)
    return <CreateProjectCard standalone organizationId={organizationId} />;

  return (
    <>
      <OrganizationTabs
        organizationId={organizationId}
        name={organization.data.name}
        active="projects"
        actions={
          <button type="button" onClick={() => setCreating(true)} className="btn-primary">
            + Create project
          </button>
        }
      />

      {creating ? (
        <div
          className="fixed inset-0 z-40 grid place-items-center bg-ink-950/70 p-4 backdrop-blur-sm"
          onClick={(e) => e.target === e.currentTarget && setCreating(false)}
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
    </>
  );
}
