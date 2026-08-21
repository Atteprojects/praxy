"use client";

import { Praxy } from "@praxy/core";
import type { Transport } from "@praxy/core";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import type { ReactNode } from "react";
import { createContext, useContext, useMemo, useState } from "react";

export interface PraxyClientConfig {
  endpoint: string;
  projectId: string;
}

interface PraxyContextValue {
  client: Praxy;
  jwt: string | undefined;
  setJwt: (jwt: string | undefined) => void;
}

const PraxyContext = createContext<PraxyContextValue | null>(null);

export interface PraxyProviderProps {
  config: PraxyClientConfig;
  /**
   * A JWT minted server-side (`POST /v1/account/jwts`, or `@praxy/nextjs`'s `createServerClient()`
   * calling it for you) — never the real session token. This is the entire server→client auth
   * bridge: every REST call and realtime subscription this provider's hooks make authenticates
   * with this JWT, never with an httpOnly session cookie a Client Component can't read anyway.
   */
  initialJwt?: string;
  /** Bring your own `QueryClient` to share a cache with the rest of the app; otherwise one is created. */
  queryClient?: QueryClient;
  /** Escape hatch, mainly for tests — a custom `Transport` instead of the default `fetch`-based one. */
  transport?: Transport;
  children: ReactNode;
}

export function PraxyProvider({ config, initialJwt, queryClient, transport, children }: PraxyProviderProps) {
  const [jwt, setJwt] = useState(initialJwt);
  const [ownQueryClient] = useState(() => queryClient ?? new QueryClient());

  const client = useMemo(
    () => new Praxy({ endpoint: config.endpoint, projectId: config.projectId, sessionToken: jwt, transport }),
    [config.endpoint, config.projectId, jwt, transport],
  );
  const value = useMemo<PraxyContextValue>(() => ({ client, jwt, setJwt }), [client, jwt]);

  return (
    <PraxyContext.Provider value={value}>
      <QueryClientProvider client={queryClient ?? ownQueryClient}>{children}</QueryClientProvider>
    </PraxyContext.Provider>
  );
}

/** The escape hatch: every service on `@praxy/core`'s `Praxy` client, JWT-authenticated, for anything a dedicated hook doesn't cover. */
export function usePraxyClient(): Praxy {
  const ctx = useContext(PraxyContext);
  if (!ctx) throw new Error("usePraxyClient() must be called within a <PraxyProvider>.");
  return ctx.client;
}

/** Read/replace the JWT this provider's client authenticates with — e.g. after a Server Action mints a fresh one. */
export function usePraxyJwt(): { jwt: string | undefined; setJwt: (jwt: string | undefined) => void } {
  const ctx = useContext(PraxyContext);
  if (!ctx) throw new Error("usePraxyJwt() must be called within a <PraxyProvider>.");
  return { jwt: ctx.jwt, setJwt: ctx.setJwt };
}
