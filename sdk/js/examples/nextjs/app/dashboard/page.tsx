import { createServerClient } from "@praxy/nextjs";
import { endpoint, projectId } from "@/lib/config";
import { todosTable } from "@/lib/db";
import { signOut } from "../actions";
import { createTodo } from "./actions";
import { LiveTodos } from "./live-todos";

const inputStyle: React.CSSProperties = {
  padding: "8px 10px",
  borderRadius: 6,
  border: "1px solid #2a2a33",
  background: "#15151b",
  color: "#e8e8ee",
};

const buttonStyle: React.CSSProperties = {
  padding: "8px 14px",
  borderRadius: 6,
  border: "none",
  background: "#635bff",
  color: "white",
  fontWeight: 600,
  cursor: "pointer",
};

export default async function DashboardPage() {
  // Server Component read: the httpOnly session cookie authorizes this directly — no JWT involved.
  const client = await createServerClient({ endpoint, projectId });
  const user = await client.account.get();
  const { rows } = await client.tables.list(todosTable, { total: false });

  return (
    <main style={{ maxWidth: 560, margin: "40px auto", padding: 24 }}>
      <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center" }}>
        <h1>Todos</h1>
        <form action={signOut}>
          <button type="submit" style={{ ...buttonStyle, background: "#2a2a33" }}>
            Sign out
          </button>
        </form>
      </div>
      <p style={{ color: "#9a9aa8" }}>Signed in as {user.email}</p>

      <section style={{ marginTop: 24 }}>
        <h2 style={{ fontSize: 16 }}>Server Component read ({rows.length})</h2>
        <ul style={{ listStyle: "none", padding: 0, display: "grid", gap: 6 }}>
          {rows.map((row) => (
            <li key={row.$id} style={{ padding: "8px 12px", borderRadius: 6, background: "#15151b", border: "1px solid #2a2a33" }}>
              {row.title}
            </li>
          ))}
          {rows.length === 0 && <li style={{ color: "#6a6a76" }}>No todos yet.</li>}
        </ul>

        <form action={createTodo} style={{ display: "flex", gap: 8, marginTop: 12 }}>
          <input name="title" type="text" placeholder="New todo" required style={{ ...inputStyle, flex: 1 }} />
          <button type="submit" style={buttonStyle}>
            Add (Server Action)
          </button>
        </form>
      </section>

      <section style={{ marginTop: 32 }}>
        <h2 style={{ fontSize: 16 }}>Live view</h2>
        <LiveTodos />
      </section>
    </main>
  );
}
