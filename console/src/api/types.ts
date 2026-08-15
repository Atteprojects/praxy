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
  githubEnabled: boolean;
  githubClientId: string | null;
  githubClientSecretSet: boolean;
  sessionLimit: number;
  passwordMinLength: number;
}

export interface ApiKey {
  id: string;
  name: string;
  scopes: string[];
  expiresAt: string | null;
  lastUsedAt: string | null;
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
