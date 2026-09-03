import { Link, Navigate, useParams } from "@tanstack/react-router";
import { useState } from "react";
import { useOrganization, useOrganizations, useProjects } from "../api/queries";
import { Badge, FullPageSpinner, IdChip, PageHeader } from "../components/ui";
import { STR } from "../strings";
import { CreateProjectCard } from "./CreateProjectCard";

/**
 * The post-login landing route. The org id is not on the session, so the console has to resolve it
 * (list orgs, take the first — there is exactly one) before it can build the URL. That resolution
 * is a spinner, never a glimpse of the project list at a bare "/": rendering here and then jumping
 * would flash a screen the user never asked for.
 *
 * "/" stays the canonical entry point — the login redirects, the logo and every "back to projects"
 * link still point at it, and bookmarks keep working — it just forwards to the resolved org.
 */
export function HomeRedirect() {
  const organizations = useOrganizations();

  if (organizations.isPending) return <FullPageSpinner />;
  if (organizations.isError) throw organizations.error;

  const organization = organizations.data.organizations[0];
  if (!organization)
    throw new Error(`This account belongs to no ${STR.organization}. The instance claim did not complete.`);

  return (
    <Navigate to="/organization/$organizationId" params={{ organizationId: organization.id }} replace />
  );
}

/** The projects list, rendered as its owning organization's page: name on top, id in the URL. */
export function OrganizationPage() {
  const { organizationId } = useParams({ strict: false }) as { organizationId: string };
  const organization = useOrganization(organizationId);
  const projects = useProjects();
  const [creating, setCreating] = useState(false);

  if (organization.isPending || projects.isPending) return <FullPageSpinner />;
  if (organization.isError) throw organization.error;
  if (projects.isError) throw projects.error;

  // Single-org today, but the page claims these projects belong to *this* org, so it filters
  // rather than trusting the list to be org-wide.
  const owned = projects.data.projects.filter((project) => project.organizationId === organizationId);

  // Empty instance: no chrome, just the create card — the Appwrite onboarding pattern, minus the
  // org ceremony. A first-run screen is the wrong place to introduce a heading nobody asked about.
  if (owned.length === 0) return <CreateProjectCard standalone />;

  return (
    <div className="mx-auto w-full max-w-5xl px-6 py-10">
      <PageHeader
        title={organization.data.name}
        chips={<IdChip id={organization.data.id} />}
        description={`${STR.projects} in this ${STR.organization}.`}
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
          <CreateProjectCard />
        </div>
      ) : null}

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
    </div>
  );
}
