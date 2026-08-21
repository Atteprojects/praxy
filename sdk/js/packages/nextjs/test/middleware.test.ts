import { NextRequest } from "next/server";
import { describe, expect, it } from "vitest";
import { praxyMiddleware } from "../src/middleware";

function requestTo(path: string, cookie?: string): NextRequest {
  const headers = cookie ? { cookie } : undefined;
  return new NextRequest(new Request(`https://app.example${path}`, { headers }));
}

describe("praxyMiddleware", () => {
  const middleware = praxyMiddleware({
    projectId: "proj_1",
    protectedPaths: ["/dashboard"],
    signInUrl: "/sign-in",
  });

  it("passes through an unprotected path with no cookie", () => {
    const response = middleware(requestTo("/"));
    expect(response.headers.get("location")).toBeNull();
  });

  it("passes through a protected path when the session cookie is present", () => {
    const response = middleware(requestTo("/dashboard", "praxy_session_proj_1=secret"));
    expect(response.headers.get("location")).toBeNull();
  });

  it("passes through a protected sub-path when the session cookie is present", () => {
    const response = middleware(requestTo("/dashboard/settings", "praxy_session_proj_1=secret"));
    expect(response.headers.get("location")).toBeNull();
  });

  it("redirects to signInUrl with ?redirectTo=<path> when the cookie is missing", () => {
    const response = middleware(requestTo("/dashboard/settings"));
    const location = new URL(response.headers.get("location")!);
    expect(location.pathname).toBe("/sign-in");
    expect(location.searchParams.get("redirectTo")).toBe("/dashboard/settings");
  });

  it("ignores a same-project-prefixed but different path (/dashboards, not /dashboard)", () => {
    const response = middleware(requestTo("/dashboards"));
    expect(response.headers.get("location")).toBeNull();
  });

  it("ignores a different project's session cookie", () => {
    const response = middleware(requestTo("/dashboard", "praxy_session_other_proj=secret"));
    expect(response.headers.get("location")).not.toBeNull();
  });
});
