import { createOAuthCallbackHandler } from "@praxy/nextjs";
import { endpoint, projectId } from "@/lib/config";

export const { GET } = createOAuthCallbackHandler({
  endpoint,
  projectId,
  redirectTo: "/dashboard",
  redirectOnError: "/",
});
