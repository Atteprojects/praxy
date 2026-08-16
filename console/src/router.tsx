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
import { ColumnsPage } from "./screens/ColumnsPage";
import { DatabaseIndexPage, DatabaseLayout } from "./screens/DatabaseLayout";
import { DatabasesPage } from "./screens/DatabasesPage";
import { FunctionDeploymentsPage } from "./screens/FunctionDeploymentsPage";
import { FunctionExecutionsPage } from "./screens/FunctionExecutionsPage";
import { FunctionSettingsPage } from "./screens/FunctionSettingsPage";
import { FunctionsPage } from "./screens/FunctionsPage";
import { IndexesPage } from "./screens/IndexesPage";
import { LoginPage } from "./screens/LoginPage";
import { MessagesPage } from "./screens/MessagesPage";
import { MessagingProvidersPage } from "./screens/MessagingProvidersPage";
import { MessagingTemplatesPage } from "./screens/MessagingTemplatesPage";
import { MessagingTopicsPage } from "./screens/MessagingTopicsPage";
import { PlatformsPage } from "./screens/PlatformsPage";
import { ProjectLayout } from "./screens/ProjectLayout";
import { ProjectListPage } from "./screens/ProjectListPage";
import { ProjectOverviewPage } from "./screens/ProjectOverviewPage";
import { RealtimeInspectorPage } from "./screens/RealtimeInspectorPage";
import { RowsPage } from "./screens/RowsPage";
import { TableSettingsPage } from "./screens/TableSettingsPage";
import { TeamDetailPage, TeamsPage } from "./screens/TeamsPage";
import { TopicSubscribersPage } from "./screens/TopicSubscribersPage";
import { UserDetailPage } from "./screens/UserDetailPage";
import { UsersPage } from "./screens/UsersPage";
import { WebhookDeliveriesPage } from "./screens/WebhookDeliveriesPage";
import { WebhooksPage } from "./screens/WebhooksPage";

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

const databasesRoute = createRoute({
  getParentRoute: () => projectRoute,
  path: "databases",
  component: DatabasesPage,
});

const databaseLayoutRoute = createRoute({
  getParentRoute: () => projectRoute,
  path: "databases/$databaseId",
  component: DatabaseLayout,
});

const databaseIndexRoute = createRoute({
  getParentRoute: () => databaseLayoutRoute,
  path: "/",
  component: DatabaseIndexPage,
});

const tableRowsRoute = createRoute({
  getParentRoute: () => databaseLayoutRoute,
  path: "tables/$tableId/rows",
  component: RowsPage,
});

const tableColumnsRoute = createRoute({
  getParentRoute: () => databaseLayoutRoute,
  path: "tables/$tableId/columns",
  component: ColumnsPage,
});

const tableIndexesRoute = createRoute({
  getParentRoute: () => databaseLayoutRoute,
  path: "tables/$tableId/indexes",
  component: IndexesPage,
});

const tableSettingsRoute = createRoute({
  getParentRoute: () => databaseLayoutRoute,
  path: "tables/$tableId/settings",
  component: TableSettingsPage,
});

const realtimeRoute = createRoute({
  getParentRoute: () => projectRoute,
  path: "realtime",
  component: RealtimeInspectorPage,
});

const webhooksRoute = createRoute({
  getParentRoute: () => projectRoute,
  path: "webhooks",
  component: WebhooksPage,
});

const webhookDeliveriesRoute = createRoute({
  getParentRoute: () => projectRoute,
  path: "webhooks/$webhookId",
  component: WebhookDeliveriesPage,
});

const functionsRoute = createRoute({
  getParentRoute: () => projectRoute,
  path: "functions",
  component: FunctionsPage,
});

const functionDeploymentsRoute = createRoute({
  getParentRoute: () => projectRoute,
  path: "functions/$functionId",
  component: FunctionDeploymentsPage,
});

const functionExecutionsRoute = createRoute({
  getParentRoute: () => projectRoute,
  path: "functions/$functionId/executions",
  component: FunctionExecutionsPage,
});

const functionSettingsRoute = createRoute({
  getParentRoute: () => projectRoute,
  path: "functions/$functionId/settings",
  component: FunctionSettingsPage,
});

const messagesRoute = createRoute({
  getParentRoute: () => projectRoute,
  path: "messaging",
  component: MessagesPage,
});

const messagingTopicsRoute = createRoute({
  getParentRoute: () => projectRoute,
  path: "messaging/topics",
  component: MessagingTopicsPage,
});

const messagingTopicSubscribersRoute = createRoute({
  getParentRoute: () => projectRoute,
  path: "messaging/topics/$topicId",
  component: TopicSubscribersPage,
});

const messagingTemplatesRoute = createRoute({
  getParentRoute: () => projectRoute,
  path: "messaging/templates",
  component: MessagingTemplatesPage,
});

const messagingProvidersRoute = createRoute({
  getParentRoute: () => projectRoute,
  path: "messaging/providers",
  component: MessagingProvidersPage,
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
      databasesRoute,
      databaseLayoutRoute.addChildren([
        databaseIndexRoute, tableRowsRoute, tableColumnsRoute, tableIndexesRoute, tableSettingsRoute,
      ]),
      realtimeRoute,
      webhooksRoute,
      webhookDeliveriesRoute,
      functionsRoute,
      functionDeploymentsRoute,
      functionExecutionsRoute,
      functionSettingsRoute,
      messagesRoute,
      messagingTopicsRoute,
      messagingTopicSubscribersRoute,
      messagingTemplatesRoute,
      messagingProvidersRoute,
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
