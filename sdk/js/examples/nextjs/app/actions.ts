"use server";

import { Praxy } from "@praxy/core";
import { clearSessionCookie, createServerClient, setSessionCookie } from "@praxy/nextjs";
import { redirect } from "next/navigation";
import { endpoint, projectId } from "@/lib/config";

/** Email sign-up — not itself part of the "done means" checklist, but needed to have an account to sign in with at all. */
export async function signUp(formData: FormData): Promise<void> {
  const email = String(formData.get("email") ?? "");
  const password = String(formData.get("password") ?? "");
  const name = String(formData.get("name") ?? "");

  const client = new Praxy({ endpoint, projectId });
  let created: Awaited<ReturnType<typeof client.account.create>>;
  try {
    created = await client.account.create({ email, password, name });
  } catch (error) {
    const type = error instanceof Error && "type" in error ? String((error as { type: unknown }).type) : "sign_up_failed";
    redirect(`/sign-up?error=${encodeURIComponent(type)}`);
  }

  await setSessionCookie({ projectId, token: created.token, expiresAt: created.session.expiresAt });
  redirect("/dashboard");
}

/** Email sign-in — the flow the example app's live verification actually exercises end to end. */
export async function signIn(formData: FormData): Promise<void> {
  const email = String(formData.get("email") ?? "");
  const password = String(formData.get("password") ?? "");

  const client = new Praxy({ endpoint, projectId });
  let created: Awaited<ReturnType<typeof client.account.createEmailSession>>;
  try {
    created = await client.account.createEmailSession({ email, password });
  } catch (error) {
    const type = error instanceof Error && "type" in error ? String((error as { type: unknown }).type) : "sign_in_failed";
    redirect(`/?error=${encodeURIComponent(type)}`);
  }

  await setSessionCookie({ projectId, token: created.token, expiresAt: created.session.expiresAt });
  redirect("/dashboard");
}

export async function signOut(): Promise<void> {
  const client = await createServerClient({ endpoint, projectId });
  try {
    await client.account.deleteSession();
  } catch {
    // Already expired/gone — clearing the cookie below is enough either way.
  }
  await clearSessionCookie(projectId);
  redirect("/");
}
