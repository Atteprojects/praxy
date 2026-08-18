import { Link, Navigate, Outlet, useNavigate } from "@tanstack/react-router";
import { useAccount, useLogout } from "./api/queries";
import { FullPageSpinner, Kbd, Logo } from "./components/ui";
import { CommandPalette } from "./palette/CommandPalette";

/** Layout for everything behind auth: top bar, palette, and the session guard. */
export function AppShell() {
  const account = useAccount();
  const logout = useLogout();
  const navigate = useNavigate();

  if (account.isPending) return <FullPageSpinner />;
  if (account.isError) throw account.error;
  if (account.data === null) return <Navigate to="/login" />;

  return (
    <div className="min-h-dvh">
      <header className="sticky top-0 z-30 flex h-14 items-center justify-between border-b border-ink-800 bg-ink-950/80 px-4 backdrop-blur sm:px-5">
        <Link to="/" aria-label="Home">
          <Logo />
        </Link>
        <div className="flex items-center gap-3">
          <span className="hidden items-center gap-1.5 text-xs text-ink-500 sm:flex">
            <Kbd>⌘K</Kbd> commands
          </span>
          <span className="max-w-48 truncate text-sm text-ink-400">{account.data.email}</span>
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
