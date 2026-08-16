import { Link, Outlet, useParams } from "@tanstack/react-router";
import type { ReactNode } from "react";
import { useCapabilities, useProject } from "../api/queries";
import { FullPageSpinner, IdChip, Kbd } from "../components/ui";
import { STR } from "../strings";

/**
 * The project chrome: sidebar with grouped services (console-design.md), content outlet.
 * Service entries appear only when the server's capabilities flag them on — the console can
 * ship ahead of or behind the API without breaking.
 */
export function ProjectLayout() {
  const { projectId } = useParams({ strict: false }) as { projectId: string };
  const project = useProject(projectId);
  const capabilities = useCapabilities();

  if (project.isPending || capabilities.isPending) return <FullPageSpinner />;
  if (project.isError) throw project.error;
  if (capabilities.isError) throw capabilities.error;

  const features = capabilities.data.features;

  return (
    <div className="flex min-h-dvh">
      <aside className="sticky top-14 flex max-h-[calc(100dvh-3.5rem)] w-52 shrink-0 flex-col overflow-y-auto border-r border-ink-800 bg-ink-900/50 px-3 py-4">
        <Link to="/" className="btn-ghost mb-4 justify-start text-xs">
          ← {STR.projects}
        </Link>
        <span className="mb-1 truncate px-3 text-sm font-semibold">{project.data.name}</span>
        <span className="mb-6 px-3">
          <IdChip id={project.data.id} />
        </span>

        <nav className="space-y-1">
          <NavEntry to="/project/$projectId" projectId={projectId} exact kbd="g o">
            {STR.overview}
          </NavEntry>

          {features.auth ? (
            <>
              <SectionLabel>Build</SectionLabel>
              <NavEntry to="/project/$projectId/auth/users" projectId={projectId} kbd="g u">
                {STR.users}
              </NavEntry>
              <NavEntry to="/project/$projectId/auth/teams" projectId={projectId} kbd="g t">
                {STR.teams}
              </NavEntry>
              <NavEntry to="/project/$projectId/auth/settings" projectId={projectId} kbd="g s">
                Auth settings
              </NavEntry>
            </>
          ) : null}

          {features.databases ? (
            <NavEntry to="/project/$projectId/databases" projectId={projectId} kbd="g d">
              {STR.databases}
            </NavEntry>
          ) : null}

          {features.realtime ? (
            <NavEntry to="/project/$projectId/realtime" projectId={projectId} kbd="g r">
              {STR.realtime}
            </NavEntry>
          ) : null}

          {features.webhooks ? (
            <NavEntry to="/project/$projectId/webhooks" projectId={projectId} kbd="g w">
              Webhooks
            </NavEntry>
          ) : null}

          {features.functions ? (
            <NavEntry to="/project/$projectId/functions" projectId={projectId} kbd="g f">
              Functions
            </NavEntry>
          ) : null}

          {features.auth ? (
            <>
              <SectionLabel>Manage</SectionLabel>
              <NavEntry to="/project/$projectId/api-keys" projectId={projectId} kbd="g k">
                API keys
              </NavEntry>
              <NavEntry to="/project/$projectId/platforms" projectId={projectId}>
                Platforms
              </NavEntry>
            </>
          ) : null}
        </nav>
      </aside>

      <main className="min-w-0 flex-1 px-8 py-10">
        <Outlet />
      </main>
    </div>
  );
}

function SectionLabel({ children }: { children: ReactNode }) {
  return (
    <span className="mt-5 mb-1 block px-3 text-[11px] font-medium uppercase tracking-widest text-ink-500">
      {children}
    </span>
  );
}

function NavEntry({
  to,
  projectId,
  exact,
  kbd,
  children,
}: {
  to: string;
  projectId: string;
  exact?: boolean;
  kbd?: string;
  children: ReactNode;
}) {
  return (
    <Link
      to={to}
      params={{ projectId }}
      activeOptions={{ exact: exact ?? false }}
      className="flex items-center justify-between rounded-lg px-3 py-2 text-sm font-medium text-ink-400 transition-colors hover:bg-ink-850 hover:text-ink-100"
      activeProps={{ className: "bg-ink-800 text-ink-100" }}
    >
      {children}
      {kbd ? <Kbd>{kbd}</Kbd> : null}
    </Link>
  );
}
