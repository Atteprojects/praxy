import { Link, Outlet, useParams } from "@tanstack/react-router";
import { useState, type ReactNode } from "react";
import { useCapabilities, useProject } from "../api/queries";
import {
  ApiKeysIcon,
  AuthSettingsIcon,
  DatabasesIcon,
  FunctionsIcon,
  MenuIcon,
  MessagingIcon,
  OverviewIcon,
  PlatformsIcon,
  RealtimeIcon,
  TeamsIcon,
  UsersIcon,
  WebhooksIcon,
} from "../components/icons";
import { FullPageSpinner, IdChip, Kbd, Sheet } from "../components/ui";
import { STR } from "../strings";

type Features = {
  auth: boolean;
  databases: boolean;
  realtime: boolean;
  webhooks: boolean;
  functions: boolean;
  messaging: boolean;
};

/**
 * The project chrome: sidebar with grouped services (console-design.md), content outlet.
 * Service entries appear only when the server's capabilities flag them on — the console can
 * ship ahead of or behind the API without breaking.
 *
 * Below `md` the sidebar becomes an off-canvas drawer (built on the shared `Sheet` primitive)
 * opened from a sticky mobile bar, since a fixed 208px sidebar leaves almost nothing for content
 * on a phone-width viewport.
 */
export function ProjectLayout() {
  const { projectId } = useParams({ strict: false }) as { projectId: string };
  const project = useProject(projectId);
  const capabilities = useCapabilities();
  const [navOpen, setNavOpen] = useState(false);

  if (project.isPending || capabilities.isPending) return <FullPageSpinner />;
  if (project.isError) throw project.error;
  if (capabilities.isError) throw capabilities.error;

  const features = capabilities.data.features;

  return (
    <div className="flex min-h-dvh">
      <aside className="sticky top-14 hidden max-h-[calc(100dvh-3.5rem)] w-52 shrink-0 flex-col border-r border-ink-800 bg-ink-900/50 px-3 py-4 md:flex">
        <ProjectNavContent projectId={projectId} projectName={project.data.name} projectIdValue={project.data.id} features={features} />
      </aside>

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
          <span className="truncate text-sm font-medium text-ink-200">{project.data.name}</span>
        </div>

        {/* Capped so tables don't stretch to unreadable line lengths on an ultrawide display,
            and centred once the viewport outgrows the cap. */}
        <main className="mx-auto max-w-[100rem] px-4 py-6 sm:px-6 sm:py-8 md:px-8 md:py-10">
          <Outlet />
        </main>
      </div>

      {navOpen ? (
        <Sheet side="left" title={project.data.name} onClose={() => setNavOpen(false)}>
          <div onClick={(e) => (e.target as HTMLElement).closest("a") && setNavOpen(false)}>
            <ProjectNavContent projectId={projectId} projectIdValue={project.data.id} features={features} compact />
          </div>
        </Sheet>
      ) : null}
    </div>
  );
}

function ProjectNavContent({
  projectId,
  projectName,
  projectIdValue,
  features,
  compact,
}: {
  projectId: string;
  projectName?: string;
  projectIdValue: string;
  features: Features;
  compact?: boolean;
}) {
  return (
    <>
      <Link to="/" className="btn-ghost mb-4 shrink-0 justify-start text-xs">
        ← {STR.projects}
      </Link>
      {!compact ? <span className="mb-1 shrink-0 truncate px-3 text-sm font-semibold">{projectName}</span> : null}
      <span className="mb-6 shrink-0 px-3">
        <IdChip id={projectIdValue} />
      </span>

      {/* Only the nav list scrolls internally if it outgrows the sidebar — the back link,
          project name and id above stay put. */}
      <nav className="min-h-0 flex-1 space-y-1 overflow-y-auto">
        <NavEntry to="/project/$projectId" projectId={projectId} exact kbd="g o" icon={<OverviewIcon />}>
          {STR.overview}
        </NavEntry>

        {features.auth ? (
          <>
            <SectionLabel>Build</SectionLabel>
            <NavEntry to="/project/$projectId/auth/users" projectId={projectId} kbd="g u" icon={<UsersIcon />}>
              {STR.users}
            </NavEntry>
            <NavEntry to="/project/$projectId/auth/teams" projectId={projectId} kbd="g t" icon={<TeamsIcon />}>
              {STR.teams}
            </NavEntry>
            <NavEntry to="/project/$projectId/auth/settings" projectId={projectId} kbd="g s" icon={<AuthSettingsIcon />}>
              Auth settings
            </NavEntry>
          </>
        ) : null}

        {features.databases ? (
          <NavEntry to="/project/$projectId/databases" projectId={projectId} kbd="g d" icon={<DatabasesIcon />}>
            {STR.databases}
          </NavEntry>
        ) : null}

        {features.realtime ? (
          <NavEntry to="/project/$projectId/realtime" projectId={projectId} kbd="g r" icon={<RealtimeIcon />}>
            {STR.realtime}
          </NavEntry>
        ) : null}

        {features.webhooks ? (
          <NavEntry to="/project/$projectId/webhooks" projectId={projectId} kbd="g w" icon={<WebhooksIcon />}>
            Webhooks
          </NavEntry>
        ) : null}

        {features.functions ? (
          <NavEntry to="/project/$projectId/functions" projectId={projectId} kbd="g f" icon={<FunctionsIcon />}>
            Functions
          </NavEntry>
        ) : null}

        {features.messaging ? (
          <NavEntry to="/project/$projectId/messaging" projectId={projectId} kbd="g m" icon={<MessagingIcon />}>
            Messaging
          </NavEntry>
        ) : null}

        {features.auth ? (
          <>
            <SectionLabel>Manage</SectionLabel>
            <NavEntry to="/project/$projectId/api-keys" projectId={projectId} kbd="g k" icon={<ApiKeysIcon />}>
              API keys
            </NavEntry>
            <NavEntry to="/project/$projectId/platforms" projectId={projectId} icon={<PlatformsIcon />}>
              Platforms
            </NavEntry>
          </>
        ) : null}
      </nav>
    </>
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
  icon,
  children,
}: {
  to: string;
  projectId: string;
  exact?: boolean;
  kbd?: string;
  icon: ReactNode;
  children: ReactNode;
}) {
  return (
    <Link
      to={to}
      params={{ projectId }}
      activeOptions={{ exact: exact ?? false }}
      className="flex items-center gap-2.5 rounded-lg px-3 py-2 text-sm font-medium text-ink-400 transition-colors hover:bg-ink-850 hover:text-ink-100"
      activeProps={{ className: "bg-ink-800 text-ink-100" }}
    >
      <span className="shrink-0 [&>svg]:size-4">{icon}</span>
      <span className="min-w-0 flex-1 truncate">{children}</span>
      {kbd ? <Kbd>{kbd}</Kbd> : null}
    </Link>
  );
}
