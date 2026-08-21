import { Praxy } from "@praxy/core";
import { cookies } from "next/headers";
import { sessionCookieName } from "./cookies.js";

export interface PraxyServerConfig {
  endpoint: string;
  projectId: string;
}

/**
 * A plain async factory — **never** a cached/module-level singleton. A client built once at
 * cold-start and reused across requests is how one request's session leaks into another's
 * response in a serverless/edge environment. Call this fresh in every Server Component, Server
 * Action, and Route Handler that needs a client; there is no safe way to memoize it across requests.
 */
export async function createServerClient(config: PraxyServerConfig): Promise<Praxy> {
  const jar = await cookies();
  const token = jar.get(sessionCookieName(config.projectId))?.value;
  return new Praxy({ endpoint: config.endpoint, projectId: config.projectId, sessionToken: token });
}

export interface PraxyApiKeyConfig {
  endpoint: string;
  projectId: string;
  /** Defaults to `process.env.PRAXY_API_KEY`. Never read from a cookie, never shipped to the client bundle. */
  apiKey?: string;
}

/**
 * Server-only, for privileged operations a signed-in user's own session shouldn't be able to do
 * (schema management, server-side user administration). Also a plain factory — an API key never
 * changes per request, but constructing fresh avoids a second code path to reason about.
 */
export function createApiKeyClient(config: PraxyApiKeyConfig): Praxy {
  const apiKey = config.apiKey ?? process.env.PRAXY_API_KEY;
  if (!apiKey) {
    throw new Error(
      "createApiKeyClient() needs an API key — pass { apiKey } explicitly, or set PRAXY_API_KEY in the environment.",
    );
  }
  return new Praxy({ endpoint: config.endpoint, projectId: config.projectId, apiKey });
}
