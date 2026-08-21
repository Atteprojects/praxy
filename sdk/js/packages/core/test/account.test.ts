import { describe, expect, it } from "vitest";
import { Praxy } from "../src/client";
import type { TransportRequest } from "../src/transport";
import { emptyResponse, FakeTransport, jsonResponse } from "./support/fake-transport";

function clientCapturing(response: ReturnType<typeof jsonResponse>) {
  let captured!: TransportRequest;
  const transport = new FakeTransport((req) => {
    captured = req;
    return response;
  });
  const client = new Praxy({ endpoint: "https://api.test", projectId: "proj_1", transport });
  return { client, captured: () => captured };
}

describe("AccountService", () => {
  it("get() → GET /v1/account", async () => {
    const user = { id: "u1", email: "a@b.com", name: "Ada", emailVerified: true, status: true, labels: [], prefs: null, createdAt: "t", updatedAt: "t" };
    const { client, captured } = clientCapturing(jsonResponse(200, user));
    const result = await client.account.get();
    expect(captured().method).toBe("GET");
    expect(captured().path).toBe("/v1/account");
    expect(result).toEqual(user);
  });

  it("create() → POST /v1/account with email/password/name", async () => {
    const session = { user: {}, session: {}, token: "tok" };
    const { client, captured } = clientCapturing(jsonResponse(201, session));
    await client.account.create({ email: "a@b.com", password: "hunter2", name: "Ada" });
    expect(captured().method).toBe("POST");
    expect(captured().path).toBe("/v1/account");
    expect(captured().body).toEqual({ email: "a@b.com", password: "hunter2", name: "Ada" });
  });

  it("createEmailSession() → POST /v1/account/sessions/email", async () => {
    const { client, captured } = clientCapturing(jsonResponse(201, { user: {}, session: {}, token: "tok" }));
    await client.account.createEmailSession({ email: "a@b.com", password: "hunter2" });
    expect(captured().path).toBe("/v1/account/sessions/email");
    expect(captured().body).toEqual({ email: "a@b.com", password: "hunter2" });
  });

  it("createOAuth2Session() → POST /v1/account/sessions/token", async () => {
    const { client, captured } = clientCapturing(jsonResponse(201, { user: {}, session: {}, token: "tok" }));
    await client.account.createOAuth2Session({ userId: "u1", secret: "s1" });
    expect(captured().path).toBe("/v1/account/sessions/token");
    expect(captured().body).toEqual({ userId: "u1", secret: "s1" });
  });

  it("deleteSession() defaults to 'current'", async () => {
    const { client, captured } = clientCapturing(emptyResponse(204));
    await client.account.deleteSession();
    expect(captured().method).toBe("DELETE");
    expect(captured().path).toBe("/v1/account/sessions/current");
  });

  it("deleteSession(id) targets a specific session", async () => {
    const { client, captured } = clientCapturing(emptyResponse(204));
    await client.account.deleteSession("sess_42");
    expect(captured().path).toBe("/v1/account/sessions/sess_42");
  });

  it("updatePrefs() wraps the map under {prefs}", async () => {
    const { client, captured } = clientCapturing(jsonResponse(200, {}));
    await client.account.updatePrefs({ theme: "dark" });
    expect(captured().path).toBe("/v1/account/prefs");
    expect(captured().body).toEqual({ prefs: { theme: "dark" } });
  });

  it("updateName() → PATCH /v1/account/name", async () => {
    const { client, captured } = clientCapturing(jsonResponse(200, {}));
    await client.account.updateName("New Name");
    expect(captured().method).toBe("PATCH");
    expect(captured().body).toEqual({ name: "New Name" });
  });

  it("updatePassword() sends oldPassword only when given", async () => {
    const { client, captured } = clientCapturing(jsonResponse(200, {}));
    await client.account.updatePassword({ password: "new-pass" });
    expect(captured().body).toEqual({ password: "new-pass" });
  });

  it("listSessions() → GET /v1/account/sessions", async () => {
    const { client, captured } = clientCapturing(jsonResponse(200, { total: 0, sessions: [] }));
    await client.account.listSessions();
    expect(captured().method).toBe("GET");
    expect(captured().path).toBe("/v1/account/sessions");
  });

  it("sendVerification() → POST /v1/account/verification with {url}", async () => {
    const { client, captured } = clientCapturing(emptyResponse(204));
    await client.account.sendVerification("https://app.example/verify");
    expect(captured().path).toBe("/v1/account/verification");
    expect(captured().body).toEqual({ url: "https://app.example/verify" });
  });

  it("confirmVerification() → PUT /v1/account/verification", async () => {
    const { client, captured } = clientCapturing(jsonResponse(200, {}));
    await client.account.confirmVerification({ userId: "u1", secret: "s1" });
    expect(captured().method).toBe("PUT");
    expect(captured().body).toEqual({ userId: "u1", secret: "s1" });
  });

  it("sendRecovery() → POST /v1/account/recovery", async () => {
    const { client, captured } = clientCapturing(emptyResponse(204));
    await client.account.sendRecovery({ email: "a@b.com", url: "https://app.example/recover" });
    expect(captured().path).toBe("/v1/account/recovery");
    expect(captured().body).toEqual({ email: "a@b.com", url: "https://app.example/recover" });
  });

  it("confirmRecovery() → PUT /v1/account/recovery", async () => {
    const { client, captured } = clientCapturing(emptyResponse(204));
    await client.account.confirmRecovery({ userId: "u1", secret: "s1", password: "new-pass" });
    expect(captured().method).toBe("PUT");
    expect(captured().body).toEqual({ userId: "u1", secret: "s1", password: "new-pass" });
  });

  it("roles() → GET /v1/account/roles", async () => {
    const { client, captured } = clientCapturing(jsonResponse(200, { roles: ["any"], principal: "guest", scopes: null }));
    const result = await client.account.roles();
    expect(captured().path).toBe("/v1/account/roles");
    expect(result.principal).toBe("guest");
  });

  it("createJwt() → POST /v1/account/jwts with optional durationSeconds", async () => {
    const { client, captured } = clientCapturing(jsonResponse(200, { jwt: "eyJ..." }));
    await client.account.createJwt(60);
    expect(captured().path).toBe("/v1/account/jwts");
    expect(captured().body).toEqual({ durationSeconds: 60 });
  });
});
