/**
 * Wire-shape interfaces, camelCase throughout (the server's `JsonSerializerOptions` uses
 * `PropertyNamingPolicy.CamelCase`). Field names verified against the committed OpenAPI snapshot
 * (`docs/openapi/v1.json`) response schemas — not guessed.
 *
 * **Optional (`foo?: T`) means "the server may omit this key", never "the server may send `null`".**
 * The API serializes with `DefaultIgnoreCondition = WhenWritingNull`, so a null property is dropped
 * from the JSON rather than written as null. Nothing here is modelled `| null`; check absence with
 * `== null` or a truthiness test, never `=== null`, which can never fire.
 *
 * Note the OpenAPI snapshot does not capture this — it lists these fields as `required` with a
 * nullable type, which is what led the first version of this file to model them as `| null`.
 */

// ---- account / sessions --------------------------------------------------------------------

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

export interface CreatedSession {
  user: AppUser;
  session: AppSession;
  token: string;
}

export interface SessionList {
  total: number;
  sessions: AppSession[];
}

export interface ResolvedRoles {
  roles: string[];
  principal: "user" | "key" | "guest";
  scopes?: string[];
}

export interface Jwt {
  jwt: string;
}

// ---- teams ----------------------------------------------------------------------------------

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

export interface AcceptedMembership {
  membership: Membership;
  session: CreatedSession;
}

// ---- tables / rows ----------------------------------------------------------------------------

/** The `$`-prefixed system fields every row carries alongside its own columns. */
export interface RowMeta {
  $id: string;
  $tableId: string;
  $databaseId: string;
  $createdAt: string;
  $updatedAt: string;
  $permissions: string[];
  /** Meters from an `orderNear` query's point — present only when the request carried `orderNear`. */
  $distance?: number;
}

export type Row<T> = RowMeta & T;

export interface RowList<T> {
  total?: number;
  rows: Row<T>[];
}

// ---- functions --------------------------------------------------------------------------------

// ---- storage ----------------------------------------------------------------------------------

/** One stored file's metadata. The bytes come back from `getFileDownload`, never inline here. */
export interface StoredFile {
  id: string;
  bucketId: string;
  name: string;
  mimeType: string;
  sizeBytes: number;
  /** What this file was actually written with — not what the server is currently configured to use. */
  chunkSizeBytes: number;
  chunkCount: number;
  /** Lowercase hex SHA-256, computed server-side while the upload streamed. */
  checksum: string;
  createdAt: string;
  updatedAt: string;
  /**
   * The file's own grants, in the same `action("role")` grammar a row's `$permissions` uses. Empty
   * unless the bucket has file security on — nothing consults them otherwise. They are *additive*:
   * a bucket-level grant reaches every file regardless of what is listed here.
   */
  $permissions: string[];
}

export interface StoredFileList {
  total: number;
  files: StoredFile[];
}

// ---- realtime ---------------------------------------------------------------------------------

export interface RealtimeTicket {
  ticket: string;
  expiresAt: string;
}

export interface FunctionExecution {
  id: string;
  trigger: string;
  async: boolean;
  status: string;
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
