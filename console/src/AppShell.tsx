import { Link, Outlet, useNavigate, useRouterState } from "@tanstack/react-router";
import { useEffect } from "react";
import { useAccount, useLogout, useProject } from "./api/queries";
import { COMMAND_SHORTCUT_LABEL, Footer, FullPageSpinner, IdChip, Kbd, Logo } from "./components/ui";
import { CommandPalette } from "./palette/CommandPalette";

/** Layout for everything behind auth: top bar, palette, and the session guard. */
export function AppShell() {
  const account = useAccount();
  const logout = useLogout();
  const navigate = useNavigate();
  const routerState = useRouterState();
  const projectId = routerState.location.pathname.match(/^\/project\/([^/]+)/)?.[1];

  // Redirect from an effect rather than by rendering <Navigate>. That component re-issues its
  // navigation whenever its props *identity* changes, and JSX hands it a new props object on
  // every render — while this shell re-renders on every router state change (useRouterState).
  // The two fed each other: render → navigate → state change → render → navigate, a loop that
  // pegged the renderer, so a signed-out visitor got a frozen spinner instead of the login page.
  useEffect(() => {
    if (account.data === null) void navigate({ to: "/login", replace: true });
  }, [account.data, navigate]);

  if (account.isPending) return <FullPageSpinner />;
  if (account.isError) throw account.error;
  // The effect above is on its way to /login; hold the spinner for the frame in between.
  if (account.data === null) return <FullPageSpinner />;

  return (
    <div className="flex min-h-dvh flex-col">
      <header className="sticky top-0 z-30 flex h-14 shrink-0 items-center justify-between border-b border-ink-800 bg-ink-950/80 px-4 backdrop-blur sm:px-5">
        <div className="flex min-w-0 items-center gap-2.5">
          <Link to="/" aria-label="Home">
            <Logo />
          </Link>
          {/* The icon rail has no room for the project's identity, so it lives here. */}
          {projectId ? <ProjectCrumb projectId={projectId} /> : null}
        </div>
        <div className="flex shrink-0 items-center gap-3">
          <span className="hidden items-center gap-1.5 text-xs text-ink-500 sm:flex">
            <Kbd>{COMMAND_SHORTCUT_LABEL}</Kbd> commands
          </span>
          <span className="hidden max-w-48 truncate text-sm text-ink-400 sm:block">{account.data.email}</span>
          <button
            type="button"
            className="btn-ghost text-xs"
            onClick={async () => {
              await logout.mutateAsync();
              await navigate({ to: "/login" });
            }}
          >
            Sign out
          </button>
        </div>
      </header>
      {/* A flex container itself (not just a flex item) so a page's own root can grow to fill it
          via its own `flex-1` — a plain height:100% here wouldn't reliably resolve, since this
          div's height only becomes definite through flex layout, not a set CSS height. That lets a
          page-local rail's height come from stretching against this (sized against the footer
          below it), rather than an independent dvh calc that ignores the footer's existence. */}
      <div className="flex flex-1 flex-col">
        <Outlet />
      </div>
      <Footer />
      <CommandPalette />
    </div>
  );
}

function ProjectCrumb({ projectId }: { projectId: string }) {
  const project = useProject(projectId);
  if (!project.data) return null;
  // Hidden on phones: the mobile bar directly below already names the project, and at 375px the
  // crumb collided with the account email.
  return (
    <span className="hidden min-w-0 items-center gap-2.5 sm:flex">
      <span className="text-ink-700" aria-hidden>
        /
      </span>
      <span className="truncate text-sm font-medium text-ink-100">{project.data.name}</span>
      <span className="hidden lg:block">
        <IdChip id={project.data.id} />
      </span>
    </span>
  );
}
