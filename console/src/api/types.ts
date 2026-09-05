/**
 * Wire shapes for the Praxy API.
 *
 * **Optional (`foo?: T`) here means "the server may omit this key", never "the server may send
 * `null`".** `Program.cs` configures minimal-API serialization with
 * `DefaultIgnoreCondition = WhenWritingNull`, so a DTO property whose value is null is dropped from
 * the JSON entirely — it arrives `undefined`. Nothing in this file is modelled `| null`, and that is
 * deliberate: a `foo === null` guard against one of these fields is dead code that silently never
 * fires, which is exactly how the Storage screens shipped
 * "Cannot read properties of undefined (reading 'join')" (PR #55).
 *
 * Note that TypeScript will *not* flag `foo === null` on an optional field — it exempts null and
 * undefined literals from its no-overlap check — so the compiler only catches the dereference half
 * of the mistake (TS18048), not the guard half. Use `== null` when you mean "absent or null".
 *
 * The one exception is dynamic row data: a null column value in `Row` is a real, present `null`,
 * because `JsonNode` contents are written verbatim and bypass `WhenWritingNull`. `Row` models
 * column values as `unknown` for that reason.
 */

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
    storage: boolean;
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
  organizationId?: string;
  lastPingAt?: string;
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
  lastActivityAt?: string;
}

export interface UserList {
  total: number;
  users: UserListEntry[];
}

export interface UserIdentity {
  id: string;
  provider: string;
  providerUid: string;
  providerEmail?: string;
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
  ip?: string;
  userAgent?: string;
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
  invitedAt?: string;
  joinedAt?: string;
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
  googleClientId?: string;
  googleClientSecretSet: boolean;
  sessionLimit: number;
  passwordMinLength: number;
}

export interface ApiKey {
  id: string;
  name: string;
  scopes: string[];
  expiresAt?: string;
  lastUsedAt?: string;
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
  hostname?: string;
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
  "string", "integer", "float", "boolean", "datetime", "email", "url", "ip", "enum", "relationship", "geo",
] as const;
export type ColumnType = (typeof COLUMN_TYPES)[number];

/** A `geo` column's value: `{"lat","lng"}`, never GeoJSON's own `[lng, lat]` array convention. */
export interface GeoPoint {
  lat: number;
  lng: number;
}

export interface ColumnSchema {
  id: string;
  tableId: string;
  key: string;
  type: ColumnType;
  required: boolean;
  array: boolean;
  size?: number;
  default: unknown;
  elements?: string[];
  /** Set only when type === "relationship": the target table's id. */
  targetTableId?: string;
  status: "available" | "processing" | "failed";
  error?: string;
  position: number;
  createdAt: string;
  updatedAt: string;
}

export interface ColumnList {
  total: number;
  columns: ColumnSchema[];
}

export const INDEX_TYPES = ["key", "unique", "fulltext", "spatial"] as const;
export type IndexType = (typeof INDEX_TYPES)[number];

export interface IndexSchema {
  id: string;
  tableId: string;
  key: string;
  type: IndexType;
  columns: string[];
  orders: string[];
  status: "available" | "processing" | "failed";
  error?: string;
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
  tableId?: string;
  indexId?: string;
  kind: string;
  status: "queued" | "processing" | "available" | "failed" | "cancelled";
  attempts: number;
  error?: string;
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
  /** Meters from an `orderNear` query's point — present only when the request carried `orderNear`. */
  $distance?: number;
  [columnKey: string]: unknown;
}

export interface RowList {
  total?: number;
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
  disabledReason?: string;
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
  lastAttemptAt?: string;
  lastStatusCode?: number;
  lastError?: string;
  redeliveredFromId?: string;
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
  statusCode?: number;
  responseBody?: string;
  error?: string;
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
  schedule?: string;
  nextScheduledRunAt?: string;
  activeDeploymentId?: string;
  isWarm: boolean;
  /** The connected GitHub repository, "owner/repo" (Functions git integration) — null until one is connected. Set together with productionBranch. */
  repositoryFullName?: string;
  /** A push to this branch of repositoryFullName builds and auto-activates; any other branch builds a deployment that finishes ready without activating. Null until a repository is connected. */
  productionBranch?: string;
  /** ApiKeyScopes granted for schedule-/event-triggered executions, injected as PRAXY_FUNCTION_API_KEY. Empty = no platform credential (deny by default). */
  platformScopes: string[];
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
export type FunctionDeploymentSource = "upload" | "git";

export interface FunctionDeployment {
  id: string;
  status: FunctionDeploymentStatus;
  sourceSizeBytes: number;
  source: FunctionDeploymentSource;
  /** Set only for a "git" deployment — the pushed commit's full SHA. */
  commitSha?: string;
  commitMessage?: string;
  /** Set only for a "git" deployment — the branch that was pushed to (may or may not be the function's production branch). */
  branch?: string;
  buildLog: string;
  error?: string;
  imageTag?: string;
  createdAt: string;
  updatedAt: string;
  activatedAt?: string;
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
  statusCode?: number;
  responseBody?: string;
  logs: string;
  errors?: string;
  durationMs?: number;
  coldStart: boolean;
  triggeredBy?: string;
  createdAt: string;
  completedAt?: string;
}

export interface FunctionExecutionList {
  total: number;
  executions: FunctionExecution[];
}

export interface FunctionTemplate {
  key: string;
  name: string;
  description: string;
  runtime: FunctionRuntime;
  entrypoint: string;
  defaultSchedule?: string;
}

export interface FunctionTemplateList {
  templates: FunctionTemplate[];
}

export interface FunctionCreatedFromTemplate {
  function: PraxyFunction;
  deployment: FunctionDeployment;
}

// ---- Sites (post-v0.1.0): Next.js hosting ----

export interface PraxySite {
  id: string;
  key: string;
  name: string;
  rootDirectory: string;
  enabled: boolean;
  activeDeploymentId?: string;
  /** Whether the active deployment's container is actually running right now — distinct from the deployment's own "ready" status, which only means "buildable." */
  isRunning: boolean;
  publicUrl: string;
  /** The connected GitHub repository, "owner/repo" (Sites Phase 4) — null until one is connected. Set together with productionBranch. */
  repositoryFullName?: string;
  /** A push to this branch of repositoryFullName builds and auto-activates; any other branch builds a preview-only deployment. Null until a repository is connected. */
  productionBranch?: string;
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
export type SiteDeploymentSource = "upload" | "git";

export interface SiteDeployment {
  id: string;
  status: SiteDeploymentStatus;
  sourceSizeBytes: number;
  source: SiteDeploymentSource;
  /** Set only for a "git" deployment — the pushed commit's full SHA. */
  commitSha?: string;
  commitMessage?: string;
  /** Set only for a "git" deployment — the branch that was pushed to (may or may not be the site's production branch). */
  branch?: string;
  buildLog: string;
  error?: string;
  imageTag?: string;
  /** This deployment's own preview URL — set once it's `ready`, regardless of whether it's the site's active deployment. Null before that. */
  previewUrl?: string;
  createdAt: string;
  updatedAt: string;
  activatedAt?: string;
}

export interface SiteDeploymentList {
  total: number;
  deployments: SiteDeployment[];
}

export type SiteDomainStatus = "pending" | "verified";

export interface SiteDomain {
  id: string;
  hostname: string;
  status: SiteDomainStatus;
  createdAt: string;
  /** Set the moment the first request through this hostname is successfully proxied — proof Caddy's on-demand TLS actually issued a cert, not just that issuance was allowed. Null while still `pending`. */
  verifiedAt?: string;
}

export interface SiteDomainList {
  total: number;
  domains: SiteDomain[];
}

export interface SiteRequestLog {
  id: string;
  method: string;
  path: string;
  statusCode: number;
  durationMs: number;
  createdAt: string;
}

export interface SiteRequestLogList {
  total: number;
  requests: SiteRequestLog[];
}

export interface SiteGitBranches {
  branches: string[];
}

export interface FunctionGitBranches {
  branches: string[];
}

// ---- Sites Phase 4: Praxy.Vcs (instance-wide GitHub App integration) ----

export interface GithubInstallation {
  id: string;
  installationId: number;
  accountLogin: string;
  accountType: string;
  createdAt: string;
}

export interface GithubInstallationList {
  total: number;
  installations: GithubInstallation[];
}

export interface GithubInstallUrl {
  url: string;
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
  username?: string;
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
  description?: string;
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
  completedAt?: string;
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
  error?: string;
  deliveredAt?: string;
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
  bucketsUsed: number;
  bucketsMax: number;
  storageBytesUsed: number;
  storageBytesMax: number;
}

// ---- Storage ----

export interface Bucket {
  id: string;
  key: string;
  name: string;
  enabled: boolean;
  /** On: files also carry their own grants, consulted when the bucket matrix doesn't already allow the action. */
  fileSecurity: boolean;
  maxFileSizeBytes: number;
  /** Absent means any type is accepted — check with `== null`, not `=== null` (see the file header). */
  allowedMimeTypes?: string[];
  /** Types this bucket serves inline instead of as a download. Always present; empty means none. */
  inlineTypes: string[];
  createdAt: string;
  updatedAt: string;
}

export interface BucketList {
  total: number;
  buckets: Bucket[];
}

export interface BucketPermissions {
  permissions: string[];
}

export interface FilePermissions {
  permissions: string[];
}

/** The types this build will serve inline — server-owned, so the console never hard-codes them. */
export interface InlineTypeList {
  types: string[];
}

export interface StoredFile {
  id: string;
  bucketId: string;
  name: string;
  mimeType: string;
  sizeBytes: number;
  /** What this file was actually written with, not what config currently says. */
  chunkSizeBytes: number;
  chunkCount: number;
  checksum: string;
  createdAt: string;
  updatedAt: string;
  /**
   * The file's own grants, same spelling and grammar as a row's. Empty whenever the bucket has
   * `fileSecurity` off — nothing consults them then.
   */
  $permissions: string[];
}

export interface StoredFileList {
  total: number;
  files: StoredFile[];
}

export interface StorageUsage {
  usedBytes: number;
  maxBytes: number;
  maxFileSizeBytes: number;
}

// ---- Audit log ----

/** Actor is opaque (`admin:<id>` or `key:<id>`) — no endpoint resolves it to a name. */
export interface AuditLogEntry {
  id: string;
  projectId?: string;
  actor: string;
  action: string;
  resource: string;
  ip?: string;
  createdAt: string;
}

export interface AuditLogList {
  total: number;
  entries: AuditLogEntry[];
}
