import { Link, Navigate, Outlet, useNavigate, useRouterState } from "@tanstack/react-router";
import { useAccount, useLogout, useProject } from "./api/queries";
import { FullPageSpinner, IdChip, Kbd, Logo } from "./components/ui";
import { CommandPalette } from "./palette/CommandPalette";

/** Layout for everything behind auth: top bar, palette, and the session guard. */
export function AppShell() {
  const account = useAccount();
  const logout = useLogout();
  const navigate = useNavigate();
  const routerState = useRouterState();
  const projectId = routerState.location.pathname.match(/^\/project\/([^/]+)/)?.[1];

  if (account.isPending) return <FullPageSpinner />;
  if (account.isError) throw account.error;
  if (account.data === null) return <Navigate to="/login" />;

  return (
    <div className="min-h-dvh">
      <header className="sticky top-0 z-30 flex h-14 items-center justify-between border-b border-ink-800 bg-ink-950/80 px-4 backdrop-blur sm:px-5">
        <div className="flex min-w-0 items-center gap-2.5">
          <Link to="/" aria-label="Home">
            <Logo />
          </Link>
          {/* The icon rail has no room for the project's identity, so it lives here. */}
          {projectId ? <ProjectCrumb projectId={projectId} /> : null}
        </div>
        <div className="flex shrink-0 items-center gap-3">
          <span className="hidden items-center gap-1.5 text-xs text-ink-500 sm:flex">
            <Kbd>⌘K</Kbd> commands
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
      <Outlet />
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
