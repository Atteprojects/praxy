import { cookies, headers } from "next/headers";
import { sessionCookieName } from "./session-cookie-name.js";

export { sessionCookieName } from "./session-cookie-name.js";

export interface SetSessionCookieInput {
  projectId: string;
  /** The real session secret (`CreatedSession.token`) — never a JWT, never handed to a Client Component. */
  token: string;
  /** ISO-8601, from `CreatedSession.session.expiresAt`. */
  expiresAt: string;
  /** Defaults to detecting HTTPS from the current request's `x-forwarded-proto` header, then `NODE_ENV`. */
  secure?: boolean;
}

/**
 * Sets the session cookie with the **exact same shape** `AppSessionCookie.Set()` uses server-side:
 * `httpOnly: true, secure: <request was https>, sameSite: 'lax', path: '/', expires: <session.expiresAt>`.
 * Getting `sameSite`/`secure` wrong here is a silent auth-never-persists bug, not a loud error — call
 * this rather than hand-rolling `cookies().set(...)` yourself.
 */
export async function setSessionCookie(input: SetSessionCookieInput): Promise<void> {
  const jar = await cookies();
  jar.set(sessionCookieName(input.projectId), input.token, {
    httpOnly: true,
    secure: input.secure ?? (await resolveIsHttpsFromHeaders()),
    sameSite: "lax",
    path: "/",
    expires: new Date(input.expiresAt),
  });
}

export async function clearSessionCookie(projectId: string): Promise<void> {
  const jar = await cookies();
  jar.delete({ name: sessionCookieName(projectId), path: "/" });
}

/** For contexts holding a `Request` directly (a Route Handler) rather than `next/headers`. */
export function resolveIsHttps(input: { forwardedProto?: string | null; urlProtocol?: string }): boolean {
  if (input.forwardedProto) return input.forwardedProto.split(",")[0]!.trim() === "https";
  if (input.urlProtocol) return input.urlProtocol === "https:";
  return process.env.NODE_ENV === "production";
}

async function resolveIsHttpsFromHeaders(): Promise<boolean> {
  const list = await headers();
  return resolveIsHttps({ forwardedProto: list.get("x-forwarded-proto") });
}
