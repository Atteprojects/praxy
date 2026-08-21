import type { Praxy } from "../client.js";
import type { AcceptedMembership, Membership, MembershipList, Team, TeamList } from "../models.js";

/**
 * The client-facing teams surface (`/v1/teams`) — matches `TeamEndpoints.cs`'s client-facing
 * routes, not the console admin equivalent. 10 methods, matching `praxy_core`'s `TeamsService`
 * (`sdk/flutter/praxy_core/lib/src/services/teams_service.dart`).
 */
export class TeamsService {
  constructor(private readonly client: Praxy) {}

  create(input: { name: string; roles?: string[] }): Promise<Team> {
    return this.client.request<Team>("POST", "/v1/teams", { body: input });
  }

  list(): Promise<TeamList> {
    return this.client.request<TeamList>("GET", "/v1/teams");
  }

  get(teamId: string): Promise<Team> {
    return this.client.request<Team>("GET", `/v1/teams/${encodeURIComponent(teamId)}`);
  }

  update(teamId: string, name: string): Promise<Team> {
    return this.client.request<Team>("PATCH", `/v1/teams/${encodeURIComponent(teamId)}`, { body: { name } });
  }

  delete(teamId: string): Promise<void> {
    return this.client.request<void>("DELETE", `/v1/teams/${encodeURIComponent(teamId)}`);
  }

  /** A session invites (requires `url`, the invitation-email redirect); a key adds the member directly. */
  createMembership(
    teamId: string,
    input: { email?: string; userId?: string; roles?: string[]; url?: string },
  ): Promise<Membership> {
    return this.client.request<Membership>("POST", `/v1/teams/${encodeURIComponent(teamId)}/memberships`, {
      body: input,
    });
  }

  listMemberships(teamId: string): Promise<MembershipList> {
    return this.client.request<MembershipList>("GET", `/v1/teams/${encodeURIComponent(teamId)}/memberships`);
  }

  updateMembershipRoles(teamId: string, membershipId: string, roles: string[]): Promise<Membership> {
    return this.client.request<Membership>(
      "PATCH",
      `/v1/teams/${encodeURIComponent(teamId)}/memberships/${encodeURIComponent(membershipId)}`,
      { body: { roles } },
    );
  }

  /** Invitation acceptance — authenticated by the emailed secret, not by a session. */
  acceptInvitation(
    teamId: string,
    membershipId: string,
    input: { userId: string; secret: string },
  ): Promise<AcceptedMembership> {
    return this.client.request<AcceptedMembership>(
      "PATCH",
      `/v1/teams/${encodeURIComponent(teamId)}/memberships/${encodeURIComponent(membershipId)}/status`,
      { body: input },
    );
  }

  deleteMembership(teamId: string, membershipId: string): Promise<void> {
    return this.client.request<void>(
      "DELETE",
      `/v1/teams/${encodeURIComponent(teamId)}/memberships/${encodeURIComponent(membershipId)}`,
    );
  }
}
