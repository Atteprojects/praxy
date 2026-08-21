import { createServerClient, PraxyProvider } from "@praxy/nextjs";
import type { ReactNode } from "react";
import { endpoint, projectId } from "@/lib/config";

export default async function DashboardLayout({ children }: { children: ReactNode }) {
  // The server→client auth bridge: mint a short-lived JWT from the real session this Server
  // Component can see (via the httpOnly cookie) and hand only *that* down to the client tree.
  // Client Components never receive the session token itself.
  const client = await createServerClient({ endpoint, projectId });
  const { jwt } = await client.account.createJwt();

  return (
    <PraxyProvider config={{ endpoint, projectId }} initialJwt={jwt}>
      {children}
    </PraxyProvider>
  );
}
