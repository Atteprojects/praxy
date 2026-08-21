import { describe, expect, it } from "vitest";
import { createOAuthCallbackHandler } from "../src/oauth-callback";
import { FakeTransport, jsonResponse } from "./support/fake-transport";

const createdSession = {
  user: { id: "u1", email: "a@b.com", name: "Ada", emailVerified: true, status: true, labels: [], prefs: null, createdAt: "t", updatedAt: "t" },
  session: { id: "s1", userId: "u1", provider: "google", ip: null, userAgent: null, current: true, expiresAt: "2026-01-01T00:00:00.000Z", createdAt: "t" },
  token: "real-session-secret",
};

describe("OAuth callback Route Handler", () => {
  it("exchanges userId/secret for a session and sets the cookie, then redirects to redirectTo", async () => {
    const transport = new FakeTransport((req) => {
      expect(req.path).toBe("/v1/account/sessions/token");
      expect(req.body).toEqual({ userId: "u1", secret: "opaque-secret" });
      return jsonResponse(201, createdSession);
    });
    const { GET } = createOAuthCallbackHandler({
      endpoint: "https://api.test",
      projectId: "proj_1",
      redirectTo: "/dashboard",
      transport,
    });

    const request = new Request("https://app.example/auth/callback?userId=u1&secret=opaque-secret", {
      headers: { "x-forwarded-proto": "https" },
    });
    const response = await GET(request);

    expect(response.status).toBe(307);
    expect(response.headers.get("location")).toBe("https://app.example/dashboard");

    const setCookie = response.headers.get("set-cookie") ?? "";
    expect(setCookie).toContain("praxy_session_proj_1=real-session-secret");
    expect(setCookie.toLowerCase()).toContain("httponly");
    expect(setCookie.toLowerCase()).toContain("samesite=lax");
    expect(setCookie).toContain("Path=/");
    expect(setCookie.toLowerCase()).toContain("secure");
  });

  it("redirects to redirectOnError with ?error=<type> when the provider reports an error", async () => {
    const transport = new FakeTransport(() => jsonResponse(200, {}));
    const { GET } = createOAuthCallbackHandler({
      endpoint: "https://api.test",
      projectId: "proj_1",
      redirectTo: "/dashboard",
      redirectOnError: "/sign-in",
      transport,
    });

    const request = new Request("https://app.example/auth/callback?error=user_oauth2_provider_error");
    const response = await GET(request);

    expect(response.status).toBe(307);
    const location = new URL(response.headers.get("location")!);
    expect(location.pathname).toBe("/sign-in");
    expect(location.searchParams.get("error")).toBe("user_oauth2_provider_error");
    expect(transport.requests).toHaveLength(0);
  });

  it("redirects to error when userId or secret is missing from the query string", async () => {
    const transport = new FakeTransport(() => jsonResponse(200, {}));
    const { GET } = createOAuthCallbackHandler({ endpoint: "https://api.test", projectId: "proj_1", transport });

    const request = new Request("https://app.example/auth/callback");
    const response = await GET(request);

    const location = new URL(response.headers.get("location")!);
    expect(location.searchParams.get("error")).toBe("user_invalid_token");
    expect(transport.requests).toHaveLength(0);
  });

  it("redirects to error when the token exchange itself fails", async () => {
    const transport = new FakeTransport(() =>
      jsonResponse(401, { message: "Invalid or expired token.", code: 401, type: "user_invalid_token", version: "v1", requestId: "r1" }),
    );
    const { GET } = createOAuthCallbackHandler({ endpoint: "https://api.test", projectId: "proj_1", transport });

    const request = new Request("https://app.example/auth/callback?userId=u1&secret=bad");
    const response = await GET(request);

    const location = new URL(response.headers.get("location")!);
    expect(location.searchParams.get("error")).toBe("user_invalid_token");
  });
});
