/**
 * Wire shapes for the Praxy API — generated from `docs/openapi/v1.json`, not hand-written.
 *
 * `console/scripts/generate-api-types.mjs` produces `./generated/schema.ts` (committed, regenerate
 * with `npm run generate:api`, verified current in CI by `npm run check:api-types`). Everything below
 * is a thin, hand-maintained re-export layer over that generated file: mostly direct aliases, plus a
 * handful of deliberate narrowings recorded inline where the schema can't express them. See
 * `docs/research/console-api-contract.md` and `docs/handoff/console-api-contract-report.md` for why
 * this file used to be 897 lines of hand-modeled shapes and isn't anymore.
 *
 * **Optional (`foo?: T`) here still means "the server may omit this key", never "the server may send
 * `null`"** — `Program.cs` configures `DefaultIgnoreCondition = WhenWritingNull`, so a null-valued
 * property is dropped from the JSON entirely rather than sent as `null`. That used to be a fact this
 * file's authors had to remember by hand; it's now enforced by `OpenApiWireNullability`
 * (`src/Praxy.Api/Infrastructure`), which corrects the OpenAPI document itself before generation ever
 * sees it. `foo === null` against an optional field here is exactly as dead as it always was — the
 * type system just backs that up now instead of relying on the header comment this replaced.
 *
 * **Ids are branded.** A `WireId` (32 lowercase hex, `Ids.Wire` on the server) and a `GuidId` (a plain
 * dashed `Guid` — today only `ConsoleAccount.id` and `OrganizationMember.userId`) are both real
 * strings at runtime but distinct, incompatible types — see `./ids.ts`. Comparing or assigning across
 * the two is a compile error, which is what Organizations Phase 2 needed and didn't have: a wire id
 * compared against a dashed guid to answer "is this row me?", silently never matching.
 *
 * **Row data is the one deliberate exception to all of the above.** A null column value in `Row` is a
 * real, present `null` — `JsonObject` contents are copied verbatim and bypass `WhenWritingNull` — so
 * the generator maps it to `Record<string, unknown>` rather than treating it as another optional-field
 * case. `Row` itself has no backing schema (its shape is dynamic — one property per column) and stays
 * hand-written below.
 */

import type { components } from "./generated/schema.ts";

type Schemas = components["schemas"];

export type ErrorEnvelope = Schemas["ErrorEnvelope"];

export type Capabilities = Schemas["CapabilitiesResponse"];

export type Account = Schemas["ConsoleAccount"];

/**
 * `role` is modelled as a bare `string` in the schema — `OrganizationResponse.Role` is a plain C#
 * `string` parameter, not an enum type, so there's nothing for the generator to narrow. Narrowed by
 * hand here since the server only ever sends one of these two values.
 */
export type Organization = Omit<Schemas["OrganizationResponse"], "role"> & {
  role: "owner" | "member";
};

export interface OrganizationList {
  total: number;
  organizations: Organization[];
}

/** See `Organization.role` above — same schema limitation, same narrowing. */
export type OrganizationMember = Omit<Schemas["OrganizationMemberResponse"], "role"> & {
  role: "owner" | "member";
};

export interface OrganizationMemberList {
  total: number;
  members: OrganizationMember[];
}

export type Project = Schemas["ProjectResponse"];

export type CreateProjectInput = Schemas["CreateProjectRequest"];

export type ProjectList = Schemas["ProjectListResponse"];

// ---- Phase 1: auth ----

/** `prefs` is redeclared `Record<string, unknown>` rather than the generated `unknown` — it's always
 *  a JSON object in practice (`AppUserResponse.From` never lets it be anything else), and callers
 *  index into it by key. */
export type AppUser = Omit<Schemas["AppUserResponse"], "prefs"> & {
  prefs: Record<string, unknown>;
};

export interface UserListEntry {
  user: AppUser;
  lastActivityAt?: string;
}

export interface UserList {
  total: number;
  users: UserListEntry[];
}

export type UserIdentity = Schemas["IdentityResponse"];

export interface UserDetail {
  user: AppUser;
  identities: UserIdentity[];
}

export type AppSession = Schemas["SessionResponse"];

export type SessionList = Schemas["SessionListResponse"];

export type Team = Schemas["TeamResponse"];

export type TeamList = Schemas["TeamListResponse"];

export type Membership = Schemas["MembershipResponse"];

export type MembershipList = Schemas["MembershipListResponse"];

export type UserMembershipList = Schemas["ConsoleMembershipListResponse"];

export type AuthSettings = Schemas["AuthSettingsResponse"];

export type ApiKey = Schemas["ApiKeyResponse"];

export type ApiKeyList = Schemas["ApiKeyListResponse"];

export type CreatedApiKey = Schemas["CreatedApiKeyResponse"];

export type Platform = Schemas["PlatformResponse"];

export type PlatformList = Schemas["PlatformListResponse"];

// ---- Phase 2: schema engine ----

export type Database = Schemas["DatabaseResponse"];

export type DatabaseList = Schemas["DatabaseListResponse"];

export type TableSchema = Schemas["TableResponse"];

export type TableList = Schemas["TableListResponse"];

/**
 * `ColumnResponse.type`/`.status`, `IndexResponse.type`/`.status`, `SchemaJobResponse.status`, every
 * `*DeploymentResponse.status`/`.source`, `FunctionExecutionResponse.trigger`/`.status`,
 * `MessageResponse.status`, `MessageTargetResponse.status`, `SiteDomainResponse.status`, and
 * `MessagingTemplateResponse.key` are all bare `string` in the schema for the same reason as
 * `Organization.role` above: the C# DTOs declare these as `string`, not an enum type, so nothing in
 * `docs/openapi/v1.json` records the closed set of values a client can rely on. Every union below is
 * hand-maintained against the server code that actually produces these strings — this is exactly the
 * kind of thing a future backend phase could close by switching these DTO properties to real C#
 * enums, which `docs/handoff/console-api-contract-report.md` records as a finding, not fixed here
 * (no wire-shape changes in this initiative). Losing one of these unions to a stale hand-edit is a
 * real risk this file already carried before generation existed; the risk hasn't gone up, but it also
 * didn't go away just because the rest of this file did.
 */
export const COLUMN_TYPES = [
  "string", "integer", "float", "boolean", "datetime", "email", "url", "ip", "enum", "relationship", "geo",
] as const;
export type ColumnType = (typeof COLUMN_TYPES)[number];

/** A `geo` column's value: `{"lat","lng"}`, never GeoJSON's own `[lng, lat]` array convention. Not a
 *  named schema of its own — it only ever appears inside a `Row`'s dynamic column data. */
export interface GeoPoint {
  lat: number;
  lng: number;
}

type SchemaEntityStatus = "available" | "processing" | "failed";

export type ColumnSchema = Omit<Schemas["ColumnResponse"], "type" | "status"> & {
  type: ColumnType;
  status: SchemaEntityStatus;
};

export interface ColumnList {
  total: number;
  columns: ColumnSchema[];
}

export const INDEX_TYPES = ["key", "unique", "fulltext", "spatial"] as const;
export type IndexType = (typeof INDEX_TYPES)[number];

export type IndexSchema = Omit<Schemas["IndexResponse"], "type" | "status"> & {
  type: IndexType;
  status: SchemaEntityStatus;
};

export interface IndexList {
  total: number;
  indexes: IndexSchema[];
}

export type TablePermissions = Schemas["TablePermissionsResponse"];

export type SchemaJob = Omit<Schemas["SchemaJobResponse"], "status"> & {
  status: "queued" | "processing" | "available" | "failed" | "cancelled";
};

export interface SchemaJobList {
  total: number;
  jobs: SchemaJob[];
}

// ---- Phase 3: data plane ----

/**
 * A row's shape is dynamic (one property per column) plus the fixed `$`-prefixed system fields, so
 * unlike everything else in this file it has no backing named schema to alias — `RowListResponse`
 * only says its rows are `JsonObject` (opaque `Record<string, unknown>`). Kept hand-written.
 */
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

/** One chip in the filter popover — a client-side concept (the query DSL's wire format), not a
 *  response shape, so it has no backing schema either. */
export interface QueryFilter {
  method: string;
  attribute?: string;
  values?: unknown[];
}

// ---- Phase 6: webhooks ----

export type Webhook = Schemas["WebhookResponse"];

export type WebhookList = Schemas["WebhookListResponse"];

export type CreatedWebhook = Schemas["CreatedWebhookResponse"];

export type WebhookDeliveryStatus = "queued" | "delivering" | "succeeded" | "failed";

export type WebhookDelivery = Omit<Schemas["WebhookDeliveryResponse"], "status"> & {
  status: WebhookDeliveryStatus;
};

export interface WebhookDeliveryList {
  total: number;
  deliveries: WebhookDelivery[];
}

export type WebhookDeliveryAttempt = Schemas["WebhookDeliveryAttemptResponse"];

/**
 * The endpoint backing this used to return an anonymous `{delivery, payload, attempts}` documented
 * (wrongly) as a bare `WebhookDeliveryResponse` — `docs/handoff/console-api-contract-report.md`
 * records it as a real finding, fixed at the source (`WebhookDeliveryDetailResponse`, a named DTO,
 * same JSON either way) rather than worked around here. `delivery`/`attempts` are redeclared against
 * this file's own narrowed `WebhookDelivery`/`WebhookDeliveryAttempt`, and `payload` as `unknown`
 * (real, present JSON content — the raw stored event, not a WhenWritingNull-governed property).
 */
export interface WebhookDeliveryDetail {
  delivery: WebhookDelivery;
  payload: unknown;
  attempts: WebhookDeliveryAttempt[];
}

// ---- Phase 7: functions ----

export const FUNCTION_RUNTIMES = ["dart", "node"] as const;
export type FunctionRuntime = (typeof FUNCTION_RUNTIMES)[number];

export type FunctionRuntimeInfo = Omit<Schemas["FunctionRuntimeResponse"], "id"> & {
  id: FunctionRuntime;
};

export interface FunctionRuntimeList {
  runtimes: FunctionRuntimeInfo[];
}

export type PraxyFunction = Omit<Schemas["FunctionResponse"], "runtime"> & {
  runtime: FunctionRuntime;
};

export interface FunctionList {
  total: number;
  functions: PraxyFunction[];
}

export type FunctionEnvVar = Schemas["FunctionEnvVarResponse"];

export type FunctionEnvVarList = Schemas["FunctionEnvVarListResponse"];

export type FunctionDeploymentStatus = "queued" | "building" | "ready" | "failed";
export type FunctionDeploymentSource = "upload" | "git";

export type FunctionDeployment = Omit<Schemas["FunctionDeploymentResponse"], "status" | "source"> & {
  status: FunctionDeploymentStatus;
  source: FunctionDeploymentSource;
};

export interface FunctionDeploymentList {
  total: number;
  deployments: FunctionDeployment[];
}

export type FunctionExecutionStatus = "waiting" | "processing" | "completed" | "failed";

export type FunctionExecution = Omit<Schemas["FunctionExecutionResponse"], "trigger" | "status"> & {
  trigger: "http" | "event" | "schedule";
  status: FunctionExecutionStatus;
};

export interface FunctionExecutionList {
  total: number;
  executions: FunctionExecution[];
}

export type FunctionTemplate = Omit<Schemas["FunctionTemplateResponse"], "runtime"> & {
  runtime: FunctionRuntime;
};

export interface FunctionTemplateList {
  templates: FunctionTemplate[];
}

export interface FunctionCreatedFromTemplate {
  function: PraxyFunction;
  deployment: FunctionDeployment;
}

// ---- Sites (post-v0.1.0): Next.js hosting ----

export type PraxySite = Schemas["SiteResponse"];

export type SiteList = Schemas["SiteListResponse"];

export type SiteEnvVar = Schemas["SiteEnvVarResponse"];

export type SiteEnvVarList = Schemas["SiteEnvVarListResponse"];

export type SiteDeploymentStatus = "queued" | "building" | "ready" | "failed";
export type SiteDeploymentSource = "upload" | "git";

export type SiteDeployment = Omit<Schemas["SiteDeploymentResponse"], "status" | "source"> & {
  status: SiteDeploymentStatus;
  source: SiteDeploymentSource;
};

export interface SiteDeploymentList {
  total: number;
  deployments: SiteDeployment[];
}

export type SiteDomainStatus = "pending" | "verified";

export type SiteDomain = Omit<Schemas["SiteDomainResponse"], "status"> & {
  status: SiteDomainStatus;
};

export interface SiteDomainList {
  total: number;
  domains: SiteDomain[];
}

export type SiteRequestLog = Schemas["SiteRequestResponse"];

export type SiteRequestLogList = Schemas["SiteRequestListResponse"];

export type SiteGitBranches = Schemas["SiteGitBranchesResponse"];

export type FunctionGitBranches = Schemas["FunctionGitBranchesResponse"];

// ---- Sites Phase 4: Praxy.Vcs (instance-wide GitHub App integration) ----

export type GithubInstallation = Schemas["VcsInstallationResponse"];

export type GithubInstallationList = Schemas["VcsInstallationListResponse"];

export type GithubInstallUrl = Schemas["VcsInstallUrlResponse"];

// ---- Phase 8: messaging ----

export type MessagingProvider = Schemas["MessagingProviderResponse"];

export type MessagingProviderList = Schemas["MessagingProviderListResponse"];

export type MessagingTopic = Schemas["MessagingTopicResponse"];

export type MessagingTopicList = Schemas["MessagingTopicListResponse"];

export type MessagingSubscriber = Schemas["MessagingSubscriberResponse"];

export type MessagingSubscriberList = Schemas["MessagingSubscriberListResponse"];

export const AUTH_TEMPLATE_KEYS = ["verification", "recovery", "invitation"] as const;
export type AuthTemplateKey = (typeof AUTH_TEMPLATE_KEYS)[number];

export type MessagingTemplate = Omit<Schemas["MessagingTemplateResponse"], "key"> & {
  key: AuthTemplateKey;
};

export interface MessagingTemplateList {
  templates: MessagingTemplate[];
}

export type MessageStatus = "processing" | "completed";

export type PraxyMessage = Omit<Schemas["MessageResponse"], "status"> & {
  status: MessageStatus;
};

export interface MessageList {
  total: number;
  messages: PraxyMessage[];
}

export type MessageTargetStatus = "queued" | "sending" | "sent" | "failed";

export type MessageTarget = Omit<Schemas["MessageTargetResponse"], "status"> & {
  status: MessageTargetStatus;
};

export interface MessageDetail {
  message: PraxyMessage;
  targets: MessageTarget[];
}

// ---- Phase 9: quotas ----

/** Usage vs. the effective limit (org override, else instance default) for this project. */
export type QuotaSnapshot = Schemas["QuotaSnapshot"];

export type ProjectOverview = Schemas["ProjectOverviewResponse"];

// ---- Storage ----

export type Bucket = Schemas["BucketResponse"];

export type BucketList = Schemas["BucketListResponse"];

export type BucketPermissions = Schemas["BucketPermissionsResponse"];

export type FilePermissions = Schemas["FilePermissionsResponse"];

/** The types this build will serve inline — server-owned, so the console never hard-codes them. */
export type InlineTypeList = Schemas["InlineTypeListResponse"];

export type StoredFile = Schemas["FileResponse"];

export type StoredFileList = Schemas["FileListResponse"];

export type StorageUsage = Schemas["StorageUsageResponse"];

export type FileDerivative = Schemas["FileDerivativeResponse"];

export type FileDerivativeList = Schemas["FileDerivativeListResponse"];

// ---- Audit log ----

/** Actor is opaque (`admin:<id>` or `key:<id>`) — no endpoint resolves it to a name. */
export type AuditLogEntry = Schemas["AuditLogEntryResponse"];

export type AuditLogList = Schemas["AuditLogListResponse"];

export type { WireId, GuidId } from "./ids.ts";
