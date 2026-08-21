import { NextResponse } from "next/server";
import type { NextRequest } from "next/server";
import { sessionCookieName } from "./session-cookie-name.js";

export interface PraxyMiddlewareConfig {
  projectId: string;
  /** Paths (and their sub-paths) requiring a signed-in session. */
  protectedPaths: string[];
  /** Where to send an unauthenticated request — `?redirectTo=<original path>` is appended. */
  signInUrl: string;
}

/**
 * Runs on Edge Runtime by default — import this from `@praxy/nextjs/middleware`, **not** the main
 * `@praxy/nextjs` entry point. The main entry re-exports all of `@praxy/react` (for the
 * `<PraxyProvider>` convenience of one install), and `<PraxyProvider>` uses `createContext`, a
 * client-only React API — importing it into `middleware.ts` is a hard Next.js build error ("You're
 * importing a module that depends on `createContext` into ... Edge Middleware"), not a warning.
 * This file itself only ever imports `next/server` and a plain string constant
 * (`sessionCookieName`, from a zero-import module so it can't drag `next/headers` in either —
 * middleware doesn't support that API at all) — nothing here could pull a Node-only API into the
 * edge bundle even transitively.
 *
 * Checks cookie **presence** only, not validity: verifying the session secret against the database
 * would mean a network round-trip on every matched request. A stale cookie still gets past this
 * gate and is caught downstream by `createServerClient()` + a real `account` call, the same
 * "optimistic check, authoritative check where it counts" split most Next.js auth middleware uses.
 */
export function praxyMiddleware(config: PraxyMiddlewareConfig) {
  const cookieName = sessionCookieName(config.projectId);

  return function middleware(request: NextRequest): NextResponse {
    const { pathname } = request.nextUrl;
    const isProtected = config.protectedPaths.some(
      (path) => pathname === path || pathname.startsWith(path.endsWith("/") ? path : `${path}/`),
    );
    if (!isProtected || request.cookies.has(cookieName)) return NextResponse.next();

    const signIn = new URL(config.signInUrl, request.url);
    signIn.searchParams.set("redirectTo", pathname);
    return NextResponse.redirect(signIn);
  };
}
