/**
 * Terminology layer: every user-facing noun lives here so renaming (or a second
 * engine vocabulary) stays a one-file change.
 */
export const STR = {
  product: "Praxy",
  project: "project",
  projects: "Projects",
  organization: "organization",
  overview: "Overview",
  users: "Users",
  teams: "Teams",
  sessions: "Sessions",
  memberships: "Memberships",
  databases: "Databases",
  database: "database",
  tables: "Tables",
  table: "table",
  columns: "Columns",
  column: "column",
  indexes: "Indexes",
  index: "index",
  rowSecurity: "Row security",
  rows: "Rows",
  row: "row",
  realtime: "Realtime",
  connections: "Connections",
} as const;
