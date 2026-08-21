import type { Praxy } from "../client.js";
import type { AppUser, CreatedSession, Jwt, ResolvedRoles, SessionList } from "../models.js";

/**
 * The app-user account surface (`/v1/account`) — matches `AccountEndpoints.cs` exactly, and
 * `praxy_core`'s `AccountService` method-for-method (`sdk/flutter/praxy_core/lib/src/services/account_service.dart`).
 * 15 methods, and nothing more — no `createAnonymousSession`, same real gap the Flutter SDK's own
 * doc comment already documents (no server route exists).
 */
export class AccountService {
  constructor(private readonly client: Praxy) {}

  get(): Promise<AppUser> {
    return this.client.request<AppUser>("GET", "/v1/account");
  }

  create(input: { email: string; password: string; name?: string }): Promise<CreatedSession> {
    return this.client.request<CreatedSession>("POST", "/v1/account", { body: input });
  }

  createEmailSession(input: { email: string; password: string }): Promise<CreatedSession> {
    return this.client.request<CreatedSession>("POST", "/v1/account/sessions/email", { body: input });
  }

  /** The token-exchange half of the OAuth flow — see `@praxy/nextjs`'s OAuth callback Route Handler. */
  createOAuth2Session(input: { userId: string; secret: string }): Promise<CreatedSession> {
    return this.client.request<CreatedSession>("POST", "/v1/account/sessions/token", { body: input });
  }

  deleteSession(sessionId = "current"): Promise<void> {
    return this.client.request<void>("DELETE", `/v1/account/sessions/${encodeURIComponent(sessionId)}`);
  }

  updatePrefs(prefs: Record<string, unknown>): Promise<AppUser> {
    return this.client.request<AppUser>("PATCH", "/v1/account/prefs", { body: { prefs } });
  }

  updateName(name: string): Promise<AppUser> {
    return this.client.request<AppUser>("PATCH", "/v1/account/name", { body: { name } });
  }

  updatePassword(input: { password: string; oldPassword?: string }): Promise<AppUser> {
    return this.client.request<AppUser>("PATCH", "/v1/account/password", { body: input });
  }

  listSessions(): Promise<SessionList> {
    return this.client.request<SessionList>("GET", "/v1/account/sessions");
  }

  sendVerification(url: string): Promise<void> {
    return this.client.request<void>("POST", "/v1/account/verification", { body: { url } });
  }

  confirmVerification(input: { userId: string; secret: string }): Promise<AppUser> {
    return this.client.request<AppUser>("PUT", "/v1/account/verification", { body: input });
  }

  sendRecovery(input: { email: string; url: string }): Promise<void> {
    return this.client.request<void>("POST", "/v1/account/recovery", { body: input });
  }

  confirmRecovery(input: { userId: string; secret: string; password: string }): Promise<void> {
    return this.client.request<void>("PUT", "/v1/account/recovery", { body: input });
  }

  roles(): Promise<ResolvedRoles> {
    return this.client.request<ResolvedRoles>("GET", "/v1/account/roles");
  }

  createJwt(durationSeconds?: number): Promise<Jwt> {
    return this.client.request<Jwt>("POST", "/v1/account/jwts", { body: { durationSeconds } });
  }
}
