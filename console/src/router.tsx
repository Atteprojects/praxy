import {
  createRootRoute,
  createRoute,
  createRouter,
  Outlet,
} from "@tanstack/react-router";
import { AppShell } from "./AppShell";
import { ErrorNote } from "./components/ui";
import { ApiKeysPage } from "./screens/ApiKeysPage";
import { AuthSettingsPage } from "./screens/AuthSettingsPage";
import { LoginPage } from "./screens/LoginPage";
import { PlatformsPage } from "./screens/PlatformsPage";
import { ProjectLayout } from "./screens/ProjectLayout";
import { ProjectListPage } from "./screens/ProjectListPage";
import { ProjectOverviewPage } from "./screens/ProjectOverviewPage";
import { TeamDetailPage, TeamsPage } from "./screens/TeamsPage";
import { UserDetailPage } from "./screens/UserDetailPage";
import { UsersPage } from "./screens/UsersPage";

const rootRoute = createRootRoute({
  component: Outlet,
  errorComponent: ({ error }) => (
    <div className="grid min-h-dvh place-items-center p-4">
      <div className="w-full max-w-md space-y-4">
        <ErrorNote message={error.message} />
        <button type="button" className="btn-primary" onClick={() => window.location.reload()}>
          Reload
        </button>
      </div>
    </div>
  ),
});

const loginRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/login",
  component: LoginPage,
});

const shellRoute = createRoute({
  getParentRoute: () => rootRoute,
  id: "shell",
  component: AppShell,
});

const projectListRoute = createRoute({
  getParentRoute: () => shellRoute,
  path: "/",
  component: ProjectListPage,
});

// Everything project-scoped renders inside the sidebar layout; entries appear as the
// capabilities endpoint turns features on.
const projectRoute = createRoute({
  getParentRoute: () => shellRoute,
  path: "/project/$projectId",
  component: ProjectLayout,
});

const projectOverviewRoute = createRoute({
  getParentRoute: () => projectRoute,
  path: "/",
  component: ProjectOverviewPage,
});

const usersRoute = createRoute({
  getParentRoute: () => projectRoute,
  path: "auth/users",
  component: UsersPage,
});

const userDetailRoute = createRoute({
  getParentRoute: () => projectRoute,
  path: "auth/users/$userId",
  component: UserDetailPage,
});

const teamsRoute = createRoute({
  getParentRoute: () => projectRoute,
  path: "auth/teams",
  component: TeamsPage,
});

const teamDetailRoute = createRoute({
  getParentRoute: () => projectRoute,
  path: "auth/teams/$teamId",
  component: TeamDetailPage,
});

const authSettingsRoute = createRoute({
  getParentRoute: () => projectRoute,
  path: "auth/settings",
  component: AuthSettingsPage,
});

const apiKeysRoute = createRoute({
  getParentRoute: () => projectRoute,
  path: "api-keys",
  component: ApiKeysPage,
});

const platformsRoute = createRoute({
  getParentRoute: () => projectRoute,
  path: "platforms",
  component: PlatformsPage,
});

const routeTree = rootRoute.addChildren([
  loginRoute,
  shellRoute.addChildren([
    projectListRoute,
    projectRoute.addChildren([
      projectOverviewRoute,
      usersRoute,
      userDetailRoute,
      teamsRoute,
      teamDetailRoute,
      authSettingsRoute,
      apiKeysRoute,
      platformsRoute,
    ]),
  ]),
]);

export const router = createRouter({
  routeTree,
  basepath: "/console",
});

declare module "@tanstack/react-router" {
  interface Register {
    router: typeof router;
  }
}
