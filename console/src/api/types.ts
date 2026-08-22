export interface ErrorEnvelope {
  message: string;
  code: number;
  type: string;
  version: string;
  requestId: string;
  fields?: Record<string, string[]>;
}

export interface Capabilities {
  version: string;
  claimed: boolean;
  setupTokenRequired: boolean;
  features: {
    auth: boolean;
    databases: boolean;
    realtime: boolean;
    messaging: boolean;
    functions: boolean;
    webhooks: boolean;
    sites: boolean;
  };
}

export interface Account {
  id: string;
  email: string;
  name: string;
  createdAt: string;
}

export interface Organization {
  id: string;
  name: string;
  createdAt: string;
}

export interface OrganizationList {
  total: number;
  organizations: Organization[];
}

export interface Project {
  id: string;
  name: string;
  organizationId: string | null;
  lastPingAt?: string | null;
  createdAt: string;
}

export interface ProjectList {
  total: number;
  projects: Project[];
}

// ---- Phase 1: auth ----

export interface AppUser {
  id: string;
  email: string;
  name: string;
  emailVerified: boolean;
  status: boolean;
  labels: string[];
  prefs: Record<string, unknown>;
  createdAt: string;
  updatedAt: string;
}

export interface UserListEntry {
  user: AppUser;
  lastActivityAt: string | null;
}

export interface UserList {
  total: number;
  users: UserListEntry[];
}

export interface UserIdentity {
  id: string;
  provider: string;
  providerUid: string;
  providerEmail: string | null;
  createdAt: string;
}

export interface UserDetail {
  user: AppUser;
  identities: UserIdentity[];
}

export interface AppSession {
  id: string;
  userId: string;
  provider: string;
  ip: string | null;
  userAgent: string | null;
  current: boolean;
  expiresAt: string;
  createdAt: string;
}

export interface SessionList {
  total: number;
  sessions: AppSession[];
}

export interface Team {
  id: string;
  name: string;
  memberCount: number;
  createdAt: string;
}

export interface TeamList {
  total: number;
  teams: Team[];
}

export interface Membership {
  id: string;
  teamId: string;
  userId: string;
  userEmail: string;
  userName: string;
  roles: string[];
  confirmed: boolean;
  invitedAt: string | null;
  joinedAt: string | null;
}

export interface MembershipList {
  total: number;
  memberships: Membership[];
}

export interface UserMembershipList {
  total: number;
  memberships: { membership: Membership; teamName: string }[];
}

export interface AuthSettings {
  emailPassword: boolean;
  googleEnabled: boolean;
  googleClientId: string | null;
  googleClientSecretSet: boolean;
  sessionLimit: number;
  passwordMinLength: number;
}

export interface ApiKey {
  id: string;
  name: string;
  scopes: string[];
  expiresAt: string | null;
  lastUsedAt: string | null;
  bypassRowPermissions: boolean;
  createdAt: string;
}

export interface ApiKeyList {
  total: number;
  keys: ApiKey[];
}

export interface CreatedApiKey {
  key: ApiKey;
  secret: string;
}

export interface Platform {
  id: string;
  type: string;
  name: string;
  hostname: string | null;
  createdAt: string;
}

export interface PlatformList {
  total: number;
  platforms: Platform[];
}

// ---- Phase 2: schema engine ----

export interface Database {
  id: string;
  key: string;
  name: string;
  createdAt: string;
}

export interface DatabaseList {
  total: number;
  databases: Database[];
}

export interface TableSchema {
  id: string;
  databaseId: string;
  key: string;
  name: string;
  rowSecurity: boolean;
  enabled: boolean;
  createdAt: string;
  updatedAt: string;
}

export interface TableList {
  total: number;
  tables: TableSchema[];
}

export const COLUMN_TYPES = [
  "string", "integer", "float", "boolean", "datetime", "email", "url", "ip", "enum",
] as const;
export type ColumnType = (typeof COLUMN_TYPES)[number];

export interface ColumnSchema {
  id: string;
  tableId: string;
  key: string;
  type: ColumnType;
  required: boolean;
  array: boolean;
  size: number | null;
  default: unknown;
  elements: string[] | null;
  status: "available" | "processing" | "failed";
  error: string | null;
  position: number;
  createdAt: string;
  updatedAt: string;
}

export interface ColumnList {
  total: number;
  columns: ColumnSchema[];
}

export const INDEX_TYPES = ["key", "unique", "fulltext"] as const;
export type IndexType = (typeof INDEX_TYPES)[number];

export interface IndexSchema {
  id: string;
  tableId: string;
  key: string;
  type: IndexType;
  columns: string[];
  orders: string[];
  status: "available" | "processing" | "failed";
  error: string | null;
  createdAt: string;
  updatedAt: string;
}

export interface IndexList {
  total: number;
  indexes: IndexSchema[];
}

export interface TablePermissions {
  rowSecurity: boolean;
  permissions: string[];
}

export interface SchemaJob {
  id: string;
  databaseId: string;
  tableId: string | null;
  indexId: string | null;
  kind: string;
  status: "queued" | "processing" | "available" | "failed" | "cancelled";
  attempts: number;
  error: string | null;
  startedAt: string;
  createdAt: string;
  updatedAt: string;
}

export interface SchemaJobList {
  total: number;
  jobs: SchemaJob[];
}

// ---- Phase 3: data plane ----

/** A row's shape is dynamic (one property per column) plus the fixed `$`-prefixed system fields. */
export interface Row {
  $id: string;
  $tableId: string;
  $databaseId: string;
  $createdAt: string;
  $updatedAt: string;
  $permissions: string[];
  [columnKey: string]: unknown;
}

export interface RowList {
  total: number | null;
  rows: Row[];
}

/** One chip in the filter popover — mirrors the query DSL's `{method, attribute, values}` shape. */
export interface QueryFilter {
  method: string;
  attribute?: string;
  values?: unknown[];
}

// ---- Phase 6: webhooks ----

export interface Webhook {
  id: string;
  name: string;
  url: string;
  events: string[];
  enabled: boolean;
  disabledReason: string | null;
  consecutiveFailures: number;
  createdAt: string;
  updatedAt: string;
}

export interface WebhookList {
  total: number;
  webhooks: Webhook[];
}

export interface CreatedWebhook {
  webhook: Webhook;
  secret: string;
}

export type WebhookDeliveryStatus = "queued" | "delivering" | "succeeded" | "failed";

export interface WebhookDelivery {
  id: string;
  eventId: string;
  eventType: string;
  status: WebhookDeliveryStatus;
  attempts: number;
  nextAttemptAt: string;
  lastAttemptAt: string | null;
  lastStatusCode: number | null;
  lastError: string | null;
  redeliveredFromId: string | null;
  createdAt: string;
}

export interface WebhookDeliveryList {
  total: number;
  deliveries: WebhookDelivery[];
}

export interface WebhookDeliveryAttempt {
  attemptNumber: number;
  startedAt: string;
  durationMs: number;
  statusCode: number | null;
  responseBody: string | null;
  error: string | null;
}

export interface WebhookDeliveryDetail {
  delivery: WebhookDelivery;
  payload: unknown;
  attempts: WebhookDeliveryAttempt[];
}

// ---- Phase 7: functions ----

export const FUNCTION_RUNTIMES = ["dart", "node"] as const;
export type FunctionRuntime = (typeof FUNCTION_RUNTIMES)[number];

export interface FunctionRuntimeInfo {
  id: FunctionRuntime;
  baseImage: string;
}

export interface FunctionRuntimeList {
  runtimes: FunctionRuntimeInfo[];
}

export interface PraxyFunction {
  id: string;
  key: string;
  name: string;
  runtime: FunctionRuntime;
  entrypoint: string;
  timeoutSeconds: number;
  enabled: boolean;
  events: string[];
  /** Roles allowed to invoke over the data plane. Empty = nobody (deny by default). */
  execute: string[];
  schedule: string | null;
  nextScheduledRunAt: string | null;
  activeDeploymentId: string | null;
  isWarm: boolean;
  createdAt: string;
  updatedAt: string;
}

export interface FunctionList {
  total: number;
  functions: PraxyFunction[];
}

export interface FunctionEnvVar {
  key: string;
  createdAt: string;
  updatedAt: string;
}

export interface FunctionEnvVarList {
  total: number;
  vars: FunctionEnvVar[];
}

export type FunctionDeploymentStatus = "queued" | "building" | "ready" | "failed";

export interface FunctionDeployment {
  id: string;
  status: FunctionDeploymentStatus;
  sourceSizeBytes: number;
  buildLog: string;
  error: string | null;
  imageTag: string | null;
  createdAt: string;
  updatedAt: string;
  activatedAt: string | null;
}

export interface FunctionDeploymentList {
  total: number;
  deployments: FunctionDeployment[];
}

export type FunctionExecutionStatus = "waiting" | "processing" | "completed" | "failed";

export interface FunctionExecution {
  id: string;
  trigger: "http" | "event" | "schedule";
  async: boolean;
  status: FunctionExecutionStatus;
  method: string;
  path: string;
  statusCode: number | null;
  responseBody: string | null;
  logs: string;
  errors: string | null;
  durationMs: number | null;
  coldStart: boolean;
  triggeredBy: string | null;
  createdAt: string;
  completedAt: string | null;
}

export interface FunctionExecutionList {
  total: number;
  executions: FunctionExecution[];
}

// ---- Sites (post-v0.1.0): Next.js hosting ----

export interface PraxySite {
  id: string;
  key: string;
  name: string;
  rootDirectory: string;
  enabled: boolean;
  activeDeploymentId: string | null;
  /** Whether the active deployment's container is actually running right now — distinct from the deployment's own "ready" status, which only means "buildable." */
  isRunning: boolean;
  /** Whether the active deployment has a captured preview screenshot yet — see `useSiteScreenshotUrl`. */
  hasScreenshot: boolean;
  publicUrl: string;
  createdAt: string;
  updatedAt: string;
}

export interface SiteList {
  total: number;
  sites: PraxySite[];
}

export interface SiteEnvVar {
  key: string;
  createdAt: string;
  updatedAt: string;
}

export interface SiteEnvVarList {
  total: number;
  vars: SiteEnvVar[];
}

export type SiteDeploymentStatus = "queued" | "building" | "ready" | "failed";

export interface SiteDeployment {
  id: string;
  status: SiteDeploymentStatus;
  sourceSizeBytes: number;
  buildLog: string;
  error: string | null;
  imageTag: string | null;
  createdAt: string;
  updatedAt: string;
  activatedAt: string | null;
}

export interface SiteDeploymentList {
  total: number;
  deployments: SiteDeployment[];
}

// ---- Phase 8: messaging ----

export interface MessagingProvider {
  id: string;
  type: string;
  name: string;
  enabled: boolean;
  isDefault: boolean;
  host: string;
  port: number;
  username: string | null;
  from: string;
  useTls: boolean;
  hasSecret: boolean;
  createdAt: string;
  updatedAt: string;
}

export interface MessagingProviderList {
  total: number;
  providers: MessagingProvider[];
}

export interface MessagingTopic {
  id: string;
  key: string;
  name: string;
  description: string | null;
  subscriberCount: number;
  createdAt: string;
  updatedAt: string;
}

export interface MessagingTopicList {
  total: number;
  topics: MessagingTopic[];
}

export interface MessagingSubscriber {
  id: string;
  userId: string;
  email: string;
  createdAt: string;
}

export interface MessagingSubscriberList {
  total: number;
  subscribers: MessagingSubscriber[];
}

export const AUTH_TEMPLATE_KEYS = ["verification", "recovery", "invitation"] as const;
export type AuthTemplateKey = (typeof AUTH_TEMPLATE_KEYS)[number];

export interface MessagingTemplate {
  key: AuthTemplateKey;
  subject: string;
  body: string;
  overridden: boolean;
}

export interface MessagingTemplateList {
  templates: MessagingTemplate[];
}

export type MessageStatus = "processing" | "completed";

export interface PraxyMessage {
  id: string;
  type: string;
  subject: string;
  body: string;
  status: MessageStatus;
  topicIds: string[];
  userIds: string[];
  createdAt: string;
  completedAt: string | null;
}

export interface MessageList {
  total: number;
  messages: PraxyMessage[];
}

export type MessageTargetStatus = "queued" | "sending" | "sent" | "failed";

export interface MessageTarget {
  id: string;
  identifier: string;
  status: MessageTargetStatus;
  error: string | null;
  deliveredAt: string | null;
  createdAt: string;
}

export interface MessageDetail {
  message: PraxyMessage;
  targets: MessageTarget[];
}

// ---- Phase 9: quotas ----

/** Usage vs. the effective limit (org override, else instance default) for this project. */
export interface QuotaSnapshot {
  projectsUsed: number;
  projectsMax: number;
  databasesUsed: number;
  databasesMax: number;
  busiestDatabaseTables: number;
  tablesPerDatabaseMax: number;
  busiestTableColumns: number;
  columnsPerTableMax: number;
  busiestTableIndexes: number;
  indexesPerTableMax: number;
  sitesUsed: number;
  sitesMax: number;
}

// ---- Audit log ----

/** Actor is opaque (`admin:<id>` or `key:<id>`) — no endpoint resolves it to a name. */
export interface AuditLogEntry {
  id: string;
  projectId: string | null;
  actor: string;
  action: string;
  resource: string;
  ip: string | null;
  createdAt: string;
}

export interface AuditLogList {
  total: number;
  entries: AuditLogEntry[];
}
