import { Link, Navigate, useNavigate } from "@tanstack/react-router";
import { useState, type FormEvent } from "react";
import { ApiError } from "../api/client";
import { useCreateOrganization, useOrganizations } from "../api/queries";
import type { Organization } from "../api/types";
import { ErrorNote, Field, FullPageSpinner, IdChip, Spinner } from "../components/ui";
import { lastRememberedOrganization } from "../organizationMemory";

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
