"use client";

import { useLiveList } from "@praxy/react";
import { todosTable } from "@/lib/db";

/**
 * The Client Component half of the demo: opens a WebSocket authenticated with the JWT
 * `DashboardLayout` minted server-side (never the real session token), and patches its row list
 * live as `dashboard/actions.ts`'s Server Action (or the console, or another tab) writes rows.
 */
export function LiveTodos() {
  const { rows, connectionState } = useLiveList(todosTable);

  return (
    <div>
      <p style={{ color: "#9a9aa8", fontSize: 13 }}>
        Realtime (Client Component) — connection: <strong>{connectionState}</strong>
      </p>
      <ul style={{ listStyle: "none", padding: 0, display: "grid", gap: 6 }}>
        {rows.map((row) => (
          <li
            key={row.$id}
            style={{
              padding: "8px 12px",
              borderRadius: 6,
              background: "#15151b",
              border: "1px solid #2a2a33",
              textDecoration: row.done ? "line-through" : "none",
              color: row.done ? "#6a6a76" : "#e8e8ee",
            }}
          >
            {row.title}
          </li>
        ))}
        {rows.length === 0 && <li style={{ color: "#6a6a76" }}>No todos yet.</li>}
      </ul>
    </div>
  );
}
