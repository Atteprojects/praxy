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

describe("TeamsService", () => {
  it("create() → POST /v1/teams", async () => {
    const { client, captured } = clientCapturing(jsonResponse(201, { id: "t1", name: "Engineering", memberCount: 1, createdAt: "t" }));
    await client.teams.create({ name: "Engineering" });
    expect(captured().method).toBe("POST");
    expect(captured().path).toBe("/v1/teams");
    expect(captured().body).toEqual({ name: "Engineering" });
  });

  it("list() → GET /v1/teams", async () => {
    const { client, captured } = clientCapturing(jsonResponse(200, { total: 0, teams: [] }));
    await client.teams.list();
    expect(captured().path).toBe("/v1/teams");
  });

  it("get() → GET /v1/teams/{id}", async () => {
    const { client, captured } = clientCapturing(jsonResponse(200, { id: "t1", name: "x", memberCount: 1, createdAt: "t" }));
    await client.teams.get("t1");
    expect(captured().path).toBe("/v1/teams/t1");
  });

  it("update() → PATCH /v1/teams/{id}", async () => {
    const { client, captured } = clientCapturing(jsonResponse(200, { id: "t1", name: "New", memberCount: 1, createdAt: "t" }));
    await client.teams.update("t1", "New");
    expect(captured().method).toBe("PATCH");
    expect(captured().body).toEqual({ name: "New" });
  });

  it("delete() → DELETE /v1/teams/{id}", async () => {
    const { client, captured } = clientCapturing(emptyResponse(204));
    await client.teams.delete("t1");
    expect(captured().method).toBe("DELETE");
    expect(captured().path).toBe("/v1/teams/t1");
  });

  it("createMembership() → POST /v1/teams/{id}/memberships", async () => {
    const membership = { id: "m1", teamId: "t1", userId: "u1", userEmail: "a@b.com", userName: "Ada", roles: [], confirmed: false, invitedAt: null, joinedAt: null };
    const { client, captured } = clientCapturing(jsonResponse(201, membership));
    await client.teams.createMembership("t1", { email: "a@b.com", url: "https://app.example/join" });
    expect(captured().path).toBe("/v1/teams/t1/memberships");
    expect(captured().body).toEqual({ email: "a@b.com", url: "https://app.example/join" });
  });

  it("listMemberships() → GET /v1/teams/{id}/memberships", async () => {
    const { client, captured } = clientCapturing(jsonResponse(200, { total: 0, memberships: [] }));
    await client.teams.listMemberships("t1");
    expect(captured().path).toBe("/v1/teams/t1/memberships");
  });

  it("updateMembershipRoles() → PATCH /v1/teams/{id}/memberships/{mid}", async () => {
    const membership = { id: "m1", teamId: "t1", userId: "u1", userEmail: "a@b.com", userName: "Ada", roles: ["owner"], confirmed: true, invitedAt: null, joinedAt: "t" };
    const { client, captured } = clientCapturing(jsonResponse(200, membership));
    await client.teams.updateMembershipRoles("t1", "m1", ["owner"]);
    expect(captured().path).toBe("/v1/teams/t1/memberships/m1");
    expect(captured().body).toEqual({ roles: ["owner"] });
  });

  it("acceptInvitation() → PATCH /v1/teams/{id}/memberships/{mid}/status", async () => {
    const { client, captured } = clientCapturing(
      jsonResponse(200, {
        membership: { id: "m1", teamId: "t1", userId: "u1", userEmail: "a@b.com", userName: "Ada", roles: [], confirmed: true, invitedAt: "t", joinedAt: "t" },
        session: { user: {}, session: {}, token: "tok" },
      }),
    );
    const result = await client.teams.acceptInvitation("t1", "m1", { userId: "u1", secret: "s1" });
    expect(captured().path).toBe("/v1/teams/t1/memberships/m1/status");
    expect(captured().body).toEqual({ userId: "u1", secret: "s1" });
    expect(result.session.token).toBe("tok");
  });

  it("deleteMembership() → DELETE /v1/teams/{id}/memberships/{mid}", async () => {
    const { client, captured } = clientCapturing(emptyResponse(204));
    await client.teams.deleteMembership("t1", "m1");
    expect(captured().method).toBe("DELETE");
    expect(captured().path).toBe("/v1/teams/t1/memberships/m1");
  });
});
