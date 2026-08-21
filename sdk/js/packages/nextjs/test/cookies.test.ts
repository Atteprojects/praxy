import { beforeEach, describe, expect, it, vi } from "vitest";

interface FakeCookie {
  value: string;
  options?: Record<string, unknown>;
}

const cookieStore = new Map<string, FakeCookie>();
let forwardedProto: string | null = null;

vi.mock("next/headers", () => ({
  cookies: async () => ({
    set: (name: string, value: string, options?: Record<string, unknown>) => {
      cookieStore.set(name, { value, options });
    },
    get: (name: string) => (cookieStore.has(name) ? { name, value: cookieStore.get(name)!.value } : undefined),
    delete: (input: string | { name: string; path?: string }) => {
      cookieStore.delete(typeof input === "string" ? input : input.name);
    },
  }),
  headers: async () => ({
    get: (name: string) => (name.toLowerCase() === "x-forwarded-proto" ? forwardedProto : null),
  }),
}));

// `vi.mock` above is hoisted above this import, so `next/headers` resolves to the fake.
import { clearSessionCookie, resolveIsHttps, sessionCookieName, setSessionCookie } from "../src/cookies";

describe("sessionCookieName", () => {
  it("matches AppSessionCookie.Name(projectId) exactly: praxy_session_<projectId>", () => {
    expect(sessionCookieName("proj_1")).toBe("praxy_session_proj_1");
  });
});

describe("setSessionCookie", () => {
  beforeEach(() => {
    cookieStore.clear();
    forwardedProto = null;
  });

  it("sets httpOnly, sameSite lax, path /, and expires from the given ISO string — matching AppSessionCookie.Set() exactly", async () => {
    await setSessionCookie({ projectId: "proj_1", token: "secret-token", expiresAt: "2026-01-01T00:00:00.000Z", secure: true });

    const cookie = cookieStore.get("praxy_session_proj_1");
    expect(cookie?.value).toBe("secret-token");
    expect(cookie?.options).toMatchObject({
      httpOnly: true,
      secure: true,
      sameSite: "lax",
      path: "/",
    });
    expect((cookie?.options?.expires as Date).toISOString()).toBe("2026-01-01T00:00:00.000Z");
  });

  it("derives secure from the x-forwarded-proto header when not given explicitly", async () => {
    forwardedProto = "https";
    await setSessionCookie({ projectId: "proj_1", token: "t", expiresAt: "2026-01-01T00:00:00.000Z" });
    expect(cookieStore.get("praxy_session_proj_1")?.options?.secure).toBe(true);

    forwardedProto = "http";
    await setSessionCookie({ projectId: "proj_1", token: "t", expiresAt: "2026-01-01T00:00:00.000Z" });
    expect(cookieStore.get("praxy_session_proj_1")?.options?.secure).toBe(false);
  });
});

describe("clearSessionCookie", () => {
  it("deletes the project-scoped cookie with path /", async () => {
    cookieStore.set("praxy_session_proj_1", { value: "x" });
    await clearSessionCookie("proj_1");
    expect(cookieStore.has("praxy_session_proj_1")).toBe(false);
  });
});

describe("resolveIsHttps", () => {
  it("prefers x-forwarded-proto over the URL's own protocol", () => {
    expect(resolveIsHttps({ forwardedProto: "https", urlProtocol: "http:" })).toBe(true);
    expect(resolveIsHttps({ forwardedProto: "http", urlProtocol: "https:" })).toBe(false);
  });

  it("falls back to the URL protocol when there is no forwarded-proto header", () => {
    expect(resolveIsHttps({ urlProtocol: "https:" })).toBe(true);
    expect(resolveIsHttps({ urlProtocol: "http:" })).toBe(false);
  });
});
