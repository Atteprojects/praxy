import { Link, Outlet, useParams } from "@tanstack/react-router";
import { useState, type ReactNode } from "react";
import { useCapabilities, useProject } from "../api/queries";
import {
  ApiKeysIcon,
  AuditIcon,
  AuthSettingsIcon,
  DatabasesIcon,
  FunctionsIcon,
  MenuIcon,
  MessagingIcon,
  OverviewIcon,
  PlatformsIcon,
  RealtimeIcon,
  SitesIcon,
  TeamsIcon,
  UsersIcon,
  WebhooksIcon,
} from "../components/icons";
import { FullPageSpinner, IdChip, Kbd, Sheet } from "../components/ui";
import { STR } from "../strings";
import { TablesPanel } from "./TablesPanel";

type Feature = "auth" | "databases" | "realtime" | "webhooks" | "functions" | "messaging" | "sites";

type NavItem = {
  to: string;
  label: string;
  icon: ReactNode;
  kbd?: string;
  exact?: boolean;
  feature?: Feature;
  /** A divider is drawn in the rail wherever the group changes. */
  group: "top" | "build" | "manage";
};

const NAV: NavItem[] = [
  { to: "/project/$projectId", label: STR.overview, icon: <OverviewIcon />, kbd: "g o", exact: true, group: "top" },
  { to: "/project/$projectId/auth/users", label: STR.users, icon: <UsersIcon />, kbd: "g u", feature: "auth", group: "build" },
  { to: "/project/$projectId/auth/teams", label: STR.teams, icon: <TeamsIcon />, kbd: "g t", feature: "auth", group: "build" },
  { to: "/project/$projectId/auth/settings", label: "Auth settings", icon: <AuthSettingsIcon />, kbd: "g s", feature: "auth", group: "build" },
  { to: "/project/$projectId/databases", label: STR.databases, icon: <DatabasesIcon />, kbd: "g d", feature: "databases", group: "build" },
  { to: "/project/$projectId/realtime", label: STR.realtime, icon: <RealtimeIcon />, kbd: "g r", feature: "realtime", group: "build" },
  { to: "/project/$projectId/webhooks", label: "Webhooks", icon: <WebhooksIcon />, kbd: "g w", feature: "webhooks", group: "build" },
  { to: "/project/$projectId/functions", label: "Functions", icon: <FunctionsIcon />, kbd: "g f", feature: "functions", group: "build" },
  { to: "/project/$projectId/sites", label: "Sites", icon: <SitesIcon />, kbd: "g i", feature: "sites", group: "build" },
  { to: "/project/$projectId/messaging", label: "Messaging", icon: <MessagingIcon />, kbd: "g m", feature: "messaging", group: "build" },
  { to: "/project/$projectId/api-keys", label: "API keys", icon: <ApiKeysIcon />, kbd: "g k", feature: "auth", group: "manage" },
  { to: "/project/$projectId/platforms", label: "Platforms", icon: <PlatformsIcon />, feature: "auth", group: "manage" },
  { to: "/project/$projectId/audit", label: "Audit log", icon: <AuditIcon />, kbd: "g a", group: "manage" },
];

/**
 * The project chrome: a 64px icon rail, an optional contextual panel (the database's tables), and
 * the content outlet.
 *
 * The rail replaced a 208px labelled sidebar. With the tables panel also on screen, the two
 * sidebars together cost 432px — 37% of a 1440px viewport was chrome before any row rendered.
 * Labels moved into hover tooltips, which carry the `g` chord too, and the project's name and id
 * moved up into the app header where there is room for them.
 *
 * Below `md` the rail is replaced by an off-canvas drawer carrying the full labelled nav: icons
 * alone are a poor trade on a phone, and the drawer costs nothing when closed.
 */
export function ProjectLayout() {
  const { projectId, databaseId } = useParams({ strict: false }) as {
    projectId: string;
    databaseId?: string;
  };
  const project = useProject(projectId);
  const capabilities = useCapabilities();
  const [navOpen, setNavOpen] = useState(false);

  if (project.isPending || capabilities.isPending) return <FullPageSpinner />;
  if (project.isError) throw project.error;
  if (capabilities.isError) throw capabilities.error;

  const features = capabilities.data.features;
  const items = NAV.filter((item) => !item.feature || features[item.feature]);
  // The tables panel is the only thing that makes room tight enough to be worth losing labels.
  const collapsed = Boolean(databaseId);

  return (
    <div className="flex min-h-dvh">
      <nav
        className={`sticky top-14 hidden h-[calc(100dvh-3.5rem)] shrink-0 flex-col border-r border-ink-800 bg-ink-900/50 py-4 md:flex ${
          collapsed ? "w-16 items-center gap-1" : "w-52 px-3"
        }`}
        aria-label="Project"
      >
        {items.map((item, i) => (
          <NavEntry
            key={item.to}
            item={item}
            projectId={projectId}
            collapsed={collapsed}
            startsGroup={i === 0 || items[i - 1].group !== item.group}
          />
        ))}
      </nav>

      {databaseId ? <TablesPanel projectId={projectId} databaseId={databaseId} /> : null}

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
            <Link to="/" className="btn-ghost mb-4 -ml-3 justify-start text-xs">
              ← {STR.projects}
            </Link>
            <span className="mb-6 block">
              <IdChip id={project.data.id} />
            </span>
            {items.map((item, i) => (
              <NavEntry
                key={item.to}
                item={item}
                projectId={projectId}
                startsGroup={i === 0 || items[i - 1].group !== item.group}
              />
            ))}
          </div>
        </Sheet>
      ) : null}
    </div>
  );
}

const GROUP_LABELS: Record<NavItem["group"], string | null> = {
  top: null,
  build: "Build",
  manage: "Manage",
};

/**
 * One nav entry, drawn either as a labelled row or as a bare glyph. Collapsed, the group boundary
 * becomes a short rule instead of a heading, and the label moves into a hover tooltip that also
 * carries the keyboard chord — otherwise the rail gives no clue what each glyph does.
 */
function NavEntry({
  item,
  projectId,
  collapsed,
  startsGroup,
}: {
  item: NavItem;
  projectId: string;
  collapsed?: boolean;
  startsGroup: boolean;
}) {
  const heading = GROUP_LABELS[item.group];

  if (collapsed) {
    return (
      <>
        {startsGroup && heading ? <span className="my-1.5 h-px w-6 shrink-0 bg-ink-800" /> : null}
        <Link
          to={item.to}
          params={{ projectId }}
          activeOptions={{ exact: item.exact ?? false }}
          aria-label={item.label}
          className="group relative flex size-10 shrink-0 items-center justify-center rounded-lg text-ink-400 transition-colors hover:bg-ink-850 hover:text-ink-100"
          activeProps={{ className: "bg-ink-800 text-ink-100" }}
        >
          <span className="[&>svg]:size-[18px]">{item.icon}</span>
          <span
            role="tooltip"
            className="pointer-events-none invisible absolute left-full z-50 ml-2 flex items-center gap-2 rounded-md border border-ink-700 bg-ink-850 px-2 py-1 text-xs whitespace-nowrap text-ink-100 opacity-0 shadow-lg shadow-black/40 transition-opacity group-hover:visible group-hover:opacity-100"
          >
            {item.label}
            {item.kbd ? <Kbd>{item.kbd}</Kbd> : null}
          </span>
        </Link>
      </>
    );
  }

  return (
    <>
      {startsGroup && heading ? (
        <span className="mt-5 mb-1 block px-3 text-[11px] font-medium tracking-widest text-ink-500 uppercase">
          {heading}
        </span>
      ) : null}
      <Link
        to={item.to}
        params={{ projectId }}
        activeOptions={{ exact: item.exact ?? false }}
        className="flex items-center gap-2.5 rounded-lg px-3 py-2 text-sm font-medium text-ink-400 transition-colors hover:bg-ink-850 hover:text-ink-100"
        activeProps={{ className: "bg-ink-800 text-ink-100" }}
      >
        <span className="shrink-0 [&>svg]:size-4">{item.icon}</span>
        <span className="min-w-0 flex-1 truncate">{item.label}</span>
        {item.kbd ? <Kbd>{item.kbd}</Kbd> : null}
      </Link>
    </>
  );
}
