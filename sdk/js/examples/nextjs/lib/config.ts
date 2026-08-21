// Next.js only inlines `NEXT_PUBLIC_*` vars into the client bundle when the access is a literal
// `process.env.NEXT_PUBLIC_X` it can statically find and replace at build time — a helper doing
// `process.env[name]` (dynamic bracket access) can't be analyzed that way and silently resolves to
// `undefined` in the browser. Every var below is a separate literal access for exactly that reason.

function required(name: string, value: string | undefined): string {
  if (!value) throw new Error(`Missing required environment variable ${name} — see .env.example.`);
  return value;
}

export const endpoint = required("NEXT_PUBLIC_PRAXY_ENDPOINT", process.env.NEXT_PUBLIC_PRAXY_ENDPOINT);
export const projectId = required("NEXT_PUBLIC_PRAXY_PROJECT_ID", process.env.NEXT_PUBLIC_PRAXY_PROJECT_ID);
export const databaseId = required("NEXT_PUBLIC_PRAXY_DATABASE_ID", process.env.NEXT_PUBLIC_PRAXY_DATABASE_ID);
export const todosTableId = required("NEXT_PUBLIC_PRAXY_TODOS_TABLE_ID", process.env.NEXT_PUBLIC_PRAXY_TODOS_TABLE_ID);
export const appUrl = process.env.NEXT_PUBLIC_APP_URL ?? "http://localhost:3000";
