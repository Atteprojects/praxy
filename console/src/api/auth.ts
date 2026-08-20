import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api } from "./client";
import type {
  ApiKeyList,
  AuthSettings,
  CreatedApiKey,
  MembershipList,
  Platform,
  PlatformList,
  SessionList,
  Team,
  TeamList,
  UserDetail,
  UserList,
  UserMembershipList,
  AppUser,
} from "./types";

/** All Phase 1 admin calls live under the project's console prefix. */
const base = (projectId: string) => `/console/projects/${projectId}`;

// ---- users --------------------------------------------------------------------------------

export function useProjectUsers(projectId: string, search: string) {
  return useQuery({
    queryKey: ["projects", projectId, "users", { search }],
    queryFn: () =>
      api<UserList>(
        `${base(projectId)}/users${search ? `?search=${encodeURIComponent(search)}` : ""}`,
      ),
    placeholderData: (previous) => previous,
  });
}

export function useUser(projectId: string, userId: string) {
  return useQuery({
    queryKey: ["projects", projectId, "users", userId],
    queryFn: () => api<UserDetail>(`${base(projectId)}/users/${userId}`),
  });
}

export function useCreateUser(projectId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: { email: string; password?: string; name?: string }) =>
      api<AppUser>(`${base(projectId)}/users`, { method: "POST", body: input }),
    onSuccess: () =>
      queryClient.invalidateQueries({ queryKey: ["projects", projectId, "users"] }),
  });
}

export function useUpdateUserStatus(projectId: string, userId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (status: boolean) =>
      api<AppUser>(`${base(projectId)}/users/${userId}/status`, {
        method: "PATCH",
        body: { status },
      }),
    onSuccess: () =>
      queryClient.invalidateQueries({ queryKey: ["projects", projectId, "users"] }),
  });
}

export function useUpdateUserLabels(projectId: string, userId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (labels: string[]) =>
      api<AppUser>(`${base(projectId)}/users/${userId}/labels`, {
        method: "PATCH",
        body: { labels },
      }),
    onSuccess: () =>
      queryClient.invalidateQueries({ queryKey: ["projects", projectId, "users"] }),
  });
}

/**
 * Changing the address resets `emailVerified` server-side — the `users/verified` permission role
 * may only sit on an address someone has proved they own. The screen says so before the click.
 */
export function useUpdateUserEmail(projectId: string, userId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (email: string) =>
      api<AppUser>(`${base(projectId)}/users/${userId}/email`, { method: "PATCH", body: { email } }),
    onSuccess: () =>
      queryClient.invalidateQueries({ queryKey: ["projects", projectId, "users"] }),
  });
}

export function useUpdateUserName(projectId: string, userId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (name: string) =>
      api<AppUser>(`${base(projectId)}/users/${userId}/name`, { method: "PATCH", body: { name } }),
    onSuccess: () =>
      queryClient.invalidateQueries({ queryKey: ["projects", projectId, "users"] }),
  });
}

/** An operator-set password revokes every session, so the sessions list refetches with it. */
export function useResetUserPassword(projectId: string, userId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (password: string) =>
      api<AppUser>(`${base(projectId)}/users/${userId}/password`, {
        method: "PATCH",
        body: { password },
      }),
    onSuccess: () =>
      queryClient.invalidateQueries({ queryKey: ["projects", projectId, "users"] }),
  });
}

export function useUpdateUserVerification(projectId: string, userId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (emailVerified: boolean) =>
      api<AppUser>(`${base(projectId)}/users/${userId}/verification`, {
        method: "PATCH",
        body: { emailVerified },
      }),
    onSuccess: () =>
      queryClient.invalidateQueries({ queryKey: ["projects", projectId, "users"] }),
  });
}

/**
 * Resends the verification mail. The redirect URL is ours to supply and is checked against the
 * project's platform allowlist server-side — an unregistered hostname comes back as a field error.
 */
export function useSendUserVerification(projectId: string, userId: string) {
  return useMutation({
    mutationFn: (url: string) =>
      api<void>(`${base(projectId)}/users/${userId}/verification`, { method: "POST", body: { url } }),
  });
}

export function useDeleteUser(projectId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (userId: string) =>
      api<void>(`${base(projectId)}/users/${userId}`, { method: "DELETE" }),
    // Skips the deleted user's own detail query. Invalidating the whole prefix refetches it,
    // which 404s and throws on the very screen that is navigating away — leaving the console
    // stranded on a route for a user that no longer exists.
    onSuccess: (_result, userId) =>
      queryClient.invalidateQueries({
        queryKey: ["projects", projectId, "users"],
        predicate: (query) => query.queryKey[3] !== userId,
      }),
  });
}

export function useUserSessions(projectId: string, userId: string) {
  return useQuery({
    queryKey: ["projects", projectId, "users", userId, "sessions"],
    queryFn: () => api<SessionList>(`${base(projectId)}/users/${userId}/sessions`),
  });
}

export function useRevokeSession(projectId: string, userId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    // "all" revokes every session at once.
    mutationFn: (sessionId: string) =>
      api<void>(
        sessionId === "all"
          ? `${base(projectId)}/users/${userId}/sessions`
          : `${base(projectId)}/users/${userId}/sessions/${sessionId}`,
        { method: "DELETE" },
      ),
    onSuccess: () =>
      queryClient.invalidateQueries({
        queryKey: ["projects", projectId, "users", userId, "sessions"],
      }),
  });
}

export function useUserMemberships(projectId: string, userId: string) {
  return useQuery({
    queryKey: ["projects", projectId, "users", userId, "memberships"],
    queryFn: () => api<UserMembershipList>(`${base(projectId)}/users/${userId}/memberships`),
  });
}

// ---- teams --------------------------------------------------------------------------------

export function useTeams(projectId: string) {
  return useQuery({
    queryKey: ["projects", projectId, "teams"],
    queryFn: () => api<TeamList>(`${base(projectId)}/teams`),
  });
}

export function useTeam(projectId: string, teamId: string) {
  return useQuery({
    queryKey: ["projects", projectId, "teams", teamId],
    queryFn: () => api<Team>(`${base(projectId)}/teams/${teamId}`),
  });
}

export function useCreateTeam(projectId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: { name: string }) =>
      api<Team>(`${base(projectId)}/teams`, { method: "POST", body: input }),
    onSuccess: () =>
      queryClient.invalidateQueries({ queryKey: ["projects", projectId, "teams"] }),
  });
}

export function useDeleteTeam(projectId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (teamId: string) =>
      api<void>(`${base(projectId)}/teams/${teamId}`, { method: "DELETE" }),
    // Skips the deleted team's own detail query. Invalidating the whole prefix refetches it,
    // which 404s and throws on the very screen that is navigating away — leaving the console
    // stranded on a route for a team that no longer exists.
    onSuccess: (_result, teamId) =>
      queryClient.invalidateQueries({
        queryKey: ["projects", projectId, "teams"],
        predicate: (query) => query.queryKey[3] !== teamId,
      }),
  });
}

export function useTeamMemberships(projectId: string, teamId: string) {
  return useQuery({
    queryKey: ["projects", projectId, "teams", teamId, "memberships"],
    queryFn: () => api<MembershipList>(`${base(projectId)}/teams/${teamId}/memberships`),
  });
}

export function useAddTeamMember(projectId: string, teamId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: { email: string; roles: string[] }) =>
      api(`${base(projectId)}/teams/${teamId}/memberships`, { method: "POST", body: input }),
    onSuccess: () => {
      void queryClient.invalidateQueries({
        queryKey: ["projects", projectId, "teams", teamId, "memberships"],
      });
      void queryClient.invalidateQueries({ queryKey: ["projects", projectId, "teams"] });
    },
  });
}

export function useUpdateMembershipRoles(projectId: string, teamId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ membershipId, roles }: { membershipId: string; roles: string[] }) =>
      api(`${base(projectId)}/teams/${teamId}/memberships/${membershipId}`, { method: "PATCH", body: { roles } }),
    onSuccess: () => {
      void queryClient.invalidateQueries({
        queryKey: ["projects", projectId, "teams", teamId, "memberships"],
      });
    },
  });
}

export function useRemoveTeamMember(projectId: string, teamId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (membershipId: string) =>
      api<void>(`${base(projectId)}/teams/${teamId}/memberships/${membershipId}`, {
        method: "DELETE",
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({
        queryKey: ["projects", projectId, "teams", teamId, "memberships"],
      });
      void queryClient.invalidateQueries({ queryKey: ["projects", projectId, "teams"] });
    },
  });
}

// ---- auth settings ------------------------------------------------------------------------

export function useAuthSettings(projectId: string) {
  return useQuery({
    queryKey: ["projects", projectId, "auth-settings"],
    queryFn: () => api<AuthSettings>(`${base(projectId)}/auth-settings`),
  });
}

export function useUpdateAuthSettings(projectId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: {
      emailPassword?: boolean;
      googleEnabled?: boolean;
      googleClientId?: string;
      googleClientSecret?: string;
      sessionLimit?: number;
      passwordMinLength?: number;
    }) => api<AuthSettings>(`${base(projectId)}/auth-settings`, { method: "PATCH", body: input }),
    onSuccess: (data) =>
      queryClient.setQueryData(["projects", projectId, "auth-settings"], data),
  });
}

// ---- API keys -----------------------------------------------------------------------------

export function useApiKeys(projectId: string) {
  return useQuery({
    queryKey: ["projects", projectId, "keys"],
    queryFn: () => api<ApiKeyList>(`${base(projectId)}/keys`),
  });
}

export function useCreateApiKey(projectId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: { name: string; scopes: string[]; bypassRowPermissions?: boolean }) =>
      api<CreatedApiKey>(`${base(projectId)}/keys`, { method: "POST", body: input }),
    onSuccess: () =>
      queryClient.invalidateQueries({ queryKey: ["projects", projectId, "keys"] }),
  });
}

export function useRevokeApiKey(projectId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (keyId: string) =>
      api<void>(`${base(projectId)}/keys/${keyId}`, { method: "DELETE" }),
    onSuccess: () =>
      queryClient.invalidateQueries({ queryKey: ["projects", projectId, "keys"] }),
  });
}

// ---- platforms ----------------------------------------------------------------------------

export function usePlatforms(projectId: string) {
  return useQuery({
    queryKey: ["projects", projectId, "platforms"],
    queryFn: () => api<PlatformList>(`${base(projectId)}/platforms`),
  });
}

export function useCreatePlatform(projectId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: { type: string; name: string; hostname?: string }) =>
      api<Platform>(`${base(projectId)}/platforms`, { method: "POST", body: input }),
    onSuccess: () =>
      queryClient.invalidateQueries({ queryKey: ["projects", projectId, "platforms"] }),
  });
}

export function useDeletePlatform(projectId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (platformId: string) =>
      api<void>(`${base(projectId)}/platforms/${platformId}`, { method: "DELETE" }),
    onSuccess: () =>
      queryClient.invalidateQueries({ queryKey: ["projects", projectId, "platforms"] }),
  });
}
