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
  };
}

export interface Account {
  id: string;
  email: string;
  name: string;
  createdAt: string;
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
