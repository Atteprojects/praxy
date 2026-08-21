export { createServerClient, createApiKeyClient } from "./server.js";
export type { PraxyServerConfig, PraxyApiKeyConfig } from "./server.js";

export { createOAuthCallbackHandler } from "./oauth-callback.js";
export type { OAuthCallbackConfig } from "./oauth-callback.js";

export { praxyMiddleware } from "./middleware.js";
export type { PraxyMiddlewareConfig } from "./middleware.js";

export { sessionCookieName, setSessionCookie, clearSessionCookie } from "./cookies.js";
export type { SetSessionCookieInput } from "./cookies.js";

// The client-side half — re-exported so a consuming app only needs `@praxy/nextjs`, not both
// `@praxy/nextjs` and `@praxy/react` as separate installs.
export * from "@praxy/react";
