import { Link, Outlet, useNavigate, useParams } from "@tanstack/react-router";
import { useEffect, useState, type FormEvent } from "react";
import { ApiError } from "../api/client";
import { wireId } from "../api/ids";
import { useCreateOrganization, useOrganization, useOrganizations } from "../api/queries";
import { ErrorNote, Field, FullPageSpinner, Modal, Sheet, Spinner } from "../components/ui";
import { MenuIcon } from "../components/icons";
import { rememberOrganization } from "../organizationMemory";

/**
 * The organization chrome: a switcher sidebar on the left, tabbed sections in the main area.
 *
 * The sidebar deliberately does *not* mirror `ProjectLayout`'s feature nav. An organization has
 * three sections and they live in tabs, so a sidebar repeating them would be two controls for one
 * job. It switches organizations instead — the role the header's `<select>` used to play, which
 * stopped being reasonable somewhere around the fourth organization and gave no sense of where you
 * were.
 *
 * Same 208px width and entry shape as `ProjectLayout`'s expanded nav, names truncating rather than
 * collapsing to initials — this page has no tables panel beside it, so none of the horizontal
 * pressure that made *that* sidebar collapse applies here. One sidebar, not two, for the same
 * reason: `ProjectLayout`'s own remarks record the two together costing 37% of a 1440px viewport.
 *
 * Below `md` it becomes a drawer, same trade as the project nav — a fixed sidebar is a poor use of
 * a phone's width, and the drawer costs nothing closed.
 */
export function OrganizationLayout() {
  const organizationId = wireId(
    (useParams({ strict: false }) as { organizationId: string }).organizationId,
  );
  const organizations = useOrganizations();
  const organization = useOrganization(organizationId);
  const [creating, setCreating] = useState(false);
  const [navOpen, setNavOpen] = useState(false);

  // Every route into an organization passes through this layout — the picker, the switcher, a
  // direct link, the home redirect — so remembering the last one lives here rather than in each page.
  useEffect(() => {
    rememberOrganization(organizationId);
  }, [organizationId]);

  if (organizations.isPending || organization.isPending) return <FullPageSpinner />;
  if (organizations.isError) throw organizations.error;
  if (organization.isError) throw organization.error;

  const all = organizations.data.organizations;

  return (
    <div className="flex flex-1">
      <nav
        className="sticky top-14 hidden w-52 shrink-0 flex-col border-r border-ink-800 bg-ink-900/50 px-3 py-4 md:flex"
        aria-label="Organizations"
      >
        <span className="mb-1 block px-3 text-[11px] font-medium tracking-widest text-ink-500 uppercase">
          Organizations
        </span>
        {all.map((org) => (
          <OrganizationEntry key={org.id} id={org.id} name={org.name} active={org.id === organizationId} />
        ))}
        <button
          type="button"
          onClick={() => setCreating(true)}
          className="mt-1 flex items-center gap-2.5 rounded-lg px-3 py-2 text-sm font-medium text-ink-500 transition-colors hover:bg-ink-850 hover:text-ink-200"
        >
          <span className="grid size-5 shrink-0 place-items-center rounded border border-dashed border-ink-700 text-[10px] leading-none">
            +
          </span>
          <span className="min-w-0 flex-1 truncate text-left">New organization</span>
        </button>
      </nav>

      <div className="min-w-0 flex-1">
        <div className="sticky top-14 z-20 flex items-center gap-3 border-b border-ink-800 bg-ink-950/80 px-4 py-2.5 backdrop-blur md:hidden">
          <button
            type="button"
            className="btn-ghost -ml-1.5 px-2"
            onClick={() => setNavOpen(true)}
            aria-label="Open menu"
          >
            <MenuIcon className="size-4.5" />
          </button>
          <span className="truncate text-sm font-medium text-ink-200">{organization.data.name}</span>
        </div>

        <main className="mx-auto max-w-6xl px-4 py-6 sm:px-6 sm:py-8 md:px-8 md:py-10">
          <Outlet />
        </main>
      </div>

      {navOpen ? (
        <Sheet side="left" title="Organizations" onClose={() => setNavOpen(false)}>
          <div onClick={(e) => (e.target as HTMLElement).closest("a") && setNavOpen(false)}>
            {all.map((org) => (
              <Link
                key={org.id}
                to="/organization/$organizationId"
                params={{ organizationId: org.id }}
                className={`-ml-3 block rounded-lg px-3 py-2 text-sm transition-colors ${
                  org.id === organizationId
                    ? "bg-ink-800/70 text-ink-100"
                    : "text-ink-400 hover:bg-ink-800/40 hover:text-ink-200"
                }`}
              >
                {org.name}
              </Link>
            ))}
            <button
              type="button"
              className="btn-ghost -ml-3 mt-2 justify-start text-xs"
              onClick={() => {
                setNavOpen(false);
                setCreating(true);
              }}
            >
              + New organization
            </button>
          </div>
        </Sheet>
      ) : null}

      {creating ? <CreateOrganizationModal onClose={() => setCreating(false)} /> : null}
    </div>
  );
}

/** Up to two initials, so "Acme Inc." reads AI and a one-word name still gets a letter. */
function initials(name: string): string {
  const words = name.trim().split(/\s+/).filter(Boolean);
  if (words.length === 0) return "?";
  if (words.length === 1) return words[0].slice(0, 2).toUpperCase();
  return (words[0][0] + words[words.length - 1][0]).toUpperCase();
}

/**
 * Same shape as `ProjectLayout`'s expanded nav entry — initials stand in for its icon, and the name
 * gets `min-w-0 flex-1 truncate`, which is what actually produces an ellipsis inside a flex row
 * (`truncate` alone can't: the flex item's default `min-width: auto` refuses to shrink below its
 * content). `title` carries the full name for the ones that do get cut.
 */
function OrganizationEntry({ id, name, active }: { id: string; name: string; active: boolean }) {
  return (
    <Link
      to="/organization/$organizationId"
      params={{ organizationId: id }}
      title={name}
      aria-current={active ? "true" : undefined}
      className={`flex items-center gap-2.5 rounded-lg px-3 py-2 text-sm font-medium transition-colors ${
        active ? "bg-ink-800 text-ink-100" : "text-ink-400 hover:bg-ink-850 hover:text-ink-100"
      }`}
    >
      <span
        className={`grid size-5 shrink-0 place-items-center rounded text-[10px] font-semibold ${
          active ? "bg-iris-500/25 text-iris-200" : "bg-ink-800 text-ink-400"
        }`}
      >
        {initials(name)}
      </span>
      <span className="min-w-0 flex-1 truncate">{name}</span>
    </Link>
  );
}

export function CreateOrganizationModal({ onClose }: { onClose: () => void }) {
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
