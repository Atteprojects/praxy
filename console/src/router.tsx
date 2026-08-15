import {
  createRootRoute,
  createRoute,
  createRouter,
  Outlet,
} from "@tanstack/react-router";
import { AppShell } from "./AppShell";
import { ErrorNote } from "./components/ui";
import { LoginPage } from "./screens/LoginPage";
import { ProjectListPage } from "./screens/ProjectListPage";
import { ProjectOverviewPage } from "./screens/ProjectOverviewPage";

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

const projectOverviewRoute = createRoute({
  getParentRoute: () => shellRoute,
  path: "/project/$projectId",
  component: ProjectOverviewPage,
});

const routeTree = rootRoute.addChildren([
  loginRoute,
  shellRoute.addChildren([projectListRoute, projectOverviewRoute]),
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
