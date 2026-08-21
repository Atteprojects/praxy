"use client";

import { useQuery } from "@tanstack/react-query";
import { usePraxyClient } from "./provider.js";

/**
 * `GET /v1/account/roles` — the one Account endpoint that works with a JWT-only client (role
 * resolution, not `AppPrincipalFilter.RequireUser`). Useful for client-side permission-aware UI.
 * There is no `useAccountProfile()`/`useSessions()` hook here on purpose: `account.get()`,
 * `listSessions()`, `updatePassword()` etc. all require a real session and 401 on a JWT — see
 * `@praxy/react`'s README for why, and do that server-side instead (Server Component/Action via
 * `@praxy/nextjs`'s `createServerClient()`).
 */
export function useRoles() {
  const client = usePraxyClient();
  return useQuery({
    queryKey: ["praxy", "roles"],
    queryFn: () => client.account.roles(),
  });
}
