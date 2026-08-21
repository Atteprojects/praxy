import { Praxy } from "@praxy/core";
import type { Transport } from "@praxy/core";
import { NextResponse } from "next/server";
import { resolveIsHttps, sessionCookieName } from "./cookies.js";

export interface OAuthCallbackConfig {
  endpoint: string;
  projectId: string;
  /** Where to send the browser after a successful sign-in. Defaults to `"/"`. */
  redirectTo?: string;
  /** Where to send the browser after a failed sign-in (`?error=<type>` is appended). Defaults to `redirectTo`. */
  redirectOnError?: string;
  /** Escape hatch, mainly for tests — a custom `Transport` instead of the default `fetch`-based one. */
  transport?: Transport;
}

/**
 * `app/auth/callback/route.ts` — set as the `success`/`failure` URL when starting the OAuth flow
 * (`GET /v1/account/sessions/oauth2/{provider}?...&success=<this route>&failure=<this route>`).
 * PKCE and the state cookie are already handled server-side (`OAuthService`); this Route Handler's
 * only job is the token-exchange half: the callback redirect carries `?userId=&secret=` in the
 * query string, **not** a ready session — exchange it at `POST /v1/account/sessions/token`
 * (`account.createOAuth2Session`) before setting the cookie.
 *
 * ```ts
 * // app/auth/callback/route.ts
 * export const { GET } = createOAuthCallbackHandler({ endpoint, projectId, redirectTo: "/dashboard" });
 * ```
 */
export function createOAuthCallbackHandler(config: OAuthCallbackConfig): {
  GET: (request: Request) => Promise<Response>;
} {
  const successPath = config.redirectTo ?? "/";
  const failurePath = config.redirectOnError ?? successPath;

  async function GET(request: Request): Promise<Response> {
    const url = new URL(request.url);
    const userId = url.searchParams.get("userId");
    const secret = url.searchParams.get("secret");
    const providerError = url.searchParams.get("error");

    const toFailure = (type: string): NextResponse => {
      const target = new URL(failurePath, url.origin);
      target.searchParams.set("error", type);
      return NextResponse.redirect(target);
    };

    if (providerError) return toFailure(providerError);
    if (!userId || !secret) return toFailure("user_invalid_token");

    const client = new Praxy({ endpoint: config.endpoint, projectId: config.projectId, transport: config.transport });
    try {
      const created = await client.account.createOAuth2Session({ userId, secret });

      const response = NextResponse.redirect(new URL(successPath, url.origin));
      response.cookies.set(sessionCookieName(config.projectId), created.token, {
        httpOnly: true,
        secure: resolveIsHttps({
          forwardedProto: request.headers.get("x-forwarded-proto"),
          urlProtocol: url.protocol,
        }),
        sameSite: "lax",
        path: "/",
        expires: new Date(created.session.expiresAt),
      });
      return response;
    } catch (error) {
      const type = error instanceof Error && "type" in error ? String((error as { type: unknown }).type) : "general_server_error";
      return toFailure(type);
    }
  }

  return { GET };
}
