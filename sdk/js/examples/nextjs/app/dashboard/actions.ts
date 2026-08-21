"use server";

import { createServerClient } from "@praxy/nextjs";
import { revalidatePath } from "next/cache";
import { endpoint, projectId } from "@/lib/config";
import { todosTable } from "@/lib/db";

/** Server Action write — the real session cookie authorizes this, not a JWT. */
export async function createTodo(formData: FormData): Promise<void> {
  const title = String(formData.get("title") ?? "").trim();
  if (!title) return;

  const client = await createServerClient({ endpoint, projectId });
  await client.tables.create(todosTable, { data: { title, done: false } });

  // Re-render the Server Component list on the next request. The Client Component's live view
  // updates over the realtime WebSocket independently, without needing this revalidation at all.
  revalidatePath("/dashboard");
}

export async function toggleTodo(rowId: string, done: boolean): Promise<void> {
  const client = await createServerClient({ endpoint, projectId });
  await client.tables.update(todosTable, rowId, { data: { done } });
  revalidatePath("/dashboard");
}
