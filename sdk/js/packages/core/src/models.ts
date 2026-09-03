/**
 * Wire-shape interfaces, camelCase throughout (the server's `JsonSerializerOptions` uses
 * `PropertyNamingPolicy.CamelCase`). Field names and nullability verified against the committed
 * OpenAPI snapshot (`docs/openapi/v1.json`) response schemas — not guessed.
 */

// ---- account / sessions --------------------------------------------------------------------

export interface AppUser {
  id: string;
  email: string;
  name: string;
  emailVerified: boolean;
  status: boolean;
  labels: string[];
  prefs: Record<string, unknown> | null;
  createdAt: string;
  updatedAt: string;
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
  scopes: string[] | null;
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
  invitedAt: string | null;
  joinedAt: string | null;
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
  total: number | null;
  rows: Row<T>[];
}

// ---- functions --------------------------------------------------------------------------------

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
