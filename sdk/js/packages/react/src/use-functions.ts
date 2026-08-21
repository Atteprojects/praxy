"use client";

import { useMutation } from "@tanstack/react-query";
import { usePraxyClient } from "./provider.js";

/**
 * Invokes a function's data-plane execution. Works with a JWT-only client — function invocation
 * is authorized by the function's `execute` role list (permission-based), the same as row access,
 * not by `AppPrincipalFilter.RequireUser` — unlike Account/Teams management (see `@praxy/react`'s README).
 */
export function useCreateExecution(functionId: string) {
  const client = usePraxyClient();
  return useMutation({
    mutationFn: (input: { method?: string; path?: string; body?: string; async?: boolean } = {}) =>
      client.functions.createExecution(functionId, input),
  });
}
