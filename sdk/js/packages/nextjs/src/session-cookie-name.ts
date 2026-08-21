/**
 * `praxy_session_<projectId>` — not ours to invent, already shipped server-side
 * (`AppSessionCookie.Name(projectId)`, `src/Praxy.Api/Infrastructure/AppPrincipal.cs`).
 * Project-scoped so one Next.js app can talk to two Praxy projects (staging/prod) without a
 * cookie-name collision.
 *
 * Deliberately its own module with zero imports (not even `next/headers`): `praxyMiddleware()`
 * (`middleware.ts`) needs this name but must never pull `next/headers` into the Edge Middleware
 * bundle — middleware doesn't support that API at all, unlike a Route Handler or Server Component.
 */
export function sessionCookieName(projectId: string): string {
  return `praxy_session_${projectId}`;
}
