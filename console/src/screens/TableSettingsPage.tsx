import { useNavigate, useParams } from "@tanstack/react-router";
import { useState } from "react";
import { useTeams } from "../api/auth";
import { ApiError } from "../api/client";
import {
  useDeleteTable, useTable, useTablePermissions, useUpdateTablePermissions,
} from "../api/databases";
import { ErrorNote, FullPageSpinner, Spinner, Toggle } from "../components/ui";
import { TableDetailHeader } from "./TableDetailHeader";

const ACTIONS = ["create", "read", "update", "delete"] as const;
type Action = (typeof ACTIONS)[number];

function parsePermissions(list: string[]): Map<string, Set<Action>> {
  const matrix = new Map<string, Set<Action>>();
  for (const permission of list) {
    const match = /^(create|read|update|delete)\("(.+)"\)$/.exec(permission);
    if (!match) continue;
    const [, action, role] = match;
    const set = matrix.get(role) ?? new Set<Action>();
    set.add(action as Action);
    matrix.set(role, set);
  }
  return matrix;
}

function withPermission(current: string[], action: Action, role: string, enabled: boolean): string[] {
  const entry = `${action}("${role}")`;
  return enabled
    ? (current.includes(entry) ? current : [...current, entry])
    : current.filter((p) => p !== entry);
}

function withoutRole(current: string[], role: string): string[] {
  return current.filter((p) => !p.endsWith(`("${role}")`));
}

export function TableSettingsPage() {
  const { projectId, databaseId, tableId } = useParams({ strict: false }) as {
    projectId: string; databaseId: string; tableId: string;
  };
  const navigate = useNavigate();
  const table = useTable(projectId, databaseId, tableId);
  const permissions = useTablePermissions(projectId, databaseId, tableId);
  const teams = useTeams(projectId);
  const update = useUpdateTablePermissions(projectId, databaseId, tableId);
  const deleteTable = useDeleteTable(projectId, databaseId);

  const [newRole, setNewRole] = useState("");
  const [teamPickerOpen, setTeamPickerOpen] = useState(false);
  const [confirmName, setConfirmName] = useState("");
  const error = update.error instanceof ApiError ? update.error : null;

  if (table.isPending || permissions.isPending) return <FullPageSpinner />;
  if (table.isError) throw table.error;
  if (permissions.isError) throw permissions.error;

  const current = permissions.data.permissions;
  const matrix = parsePermissions(current);
  const roles = [...matrix.keys()];

  function setCell(role: string, action: Action, enabled: boolean) {
    update.mutate({ permissions: withPermission(current, action, role, enabled) });
  }

  function removeRole(role: string) {
    update.mutate({ permissions: withoutRole(current, role) });
  }

  function addRole() {
    const role = newRole.trim();
    if (!role || matrix.has(role)) return;
    update.mutate({ permissions: [...current, `read("${role}")`] });
    setNewRole("");
  }

  function applyPreset(preset: "public-read" | "owner-only") {
    if (preset === "public-read") {
      update.mutate({ rowSecurity: false, permissions: ['read("any")'] });
    } else {
      update.mutate({ rowSecurity: true, permissions: ['create("users")'] });
    }
  }

  function applyTeamAccess(teamId: string) {
    update.mutate({ rowSecurity: false, permissions: [`read("team:${teamId}")`, `write("team:${teamId}")`] });
    setTeamPickerOpen(false);
  }

  async function onDeleteTable() {
    await deleteTable.mutateAsync(tableId);
    await navigate({ to: "/project/$projectId/databases/$databaseId", params: { projectId, databaseId } });
  }

  return (
    <div>
      <TableDetailHeader projectId={projectId} databaseId={databaseId} table={table.data} active="settings" />

      <div className="max-w-3xl space-y-8">
        <section className="surface p-5">
          <h2 className="mb-1 text-sm font-medium text-ink-100">Row security</h2>
          <p className="mb-3 text-xs text-ink-500">
            Off (default): only the table-level matrix below governs access — every row is treated the
            same. On: rows also carry their own per-row grants, set when the row is created, and the
            table-level matrix becomes the fallback for actions no row grant covers.
          </p>
          <Toggle
            checked={table.data.rowSecurity}
            onChange={(value) => update.mutate({ rowSecurity: value })}
            label="Enable row security"
          />
        </section>

        <section className="surface p-5">
          <div className="mb-4 flex items-center justify-between">
            <h2 className="text-sm font-medium text-ink-100">Permissions</h2>
            <div className="flex flex-wrap gap-2">
              <button type="button" className="btn-ghost border border-ink-700 text-xs" onClick={() => applyPreset("public-read")}>
                Public read
              </button>
              <button type="button" className="btn-ghost border border-ink-700 text-xs" onClick={() => applyPreset("owner-only")}>
                Owner only
              </button>
              <button
                type="button"
                className="btn-ghost border border-ink-700 text-xs"
                onClick={() => setTeamPickerOpen((v) => !v)}
              >
                Team access
              </button>
            </div>
          </div>

          {teamPickerOpen ? (
            <div className="mb-4 flex items-center gap-2 rounded-lg border border-ink-700 bg-ink-950 p-2">
              <span className="text-xs text-ink-500">Grant full access to:</span>
              <select
                className="input-base flex-1"
                defaultValue=""
                onChange={(e) => e.target.value && applyTeamAccess(e.target.value)}
              >
                <option value="" disabled>
                  {teams.isPending ? "Loading teams…" : "Select a team…"}
                </option>
                {teams.data?.teams.map((t) => (
                  <option key={t.id} value={t.id}>
                    {t.name}
                  </option>
                ))}
              </select>
            </div>
          ) : null}

          {error ? <div className="mb-3"><ErrorNote message={error.message} /></div> : null}

          <div className="overflow-x-auto">
            <table className="w-full text-left text-sm">
              <thead>
                <tr className="border-b border-ink-800 text-xs text-ink-500 uppercase">
                  <th className="py-2 pr-4 font-medium">Role</th>
                  {ACTIONS.map((action) => (
                    <th key={action} className="px-2 py-2 text-center font-medium">
                      {action}
                    </th>
                  ))}
                  <th />
                </tr>
              </thead>
              <tbody className="divide-y divide-ink-800/60">
                {roles.length === 0 ? (
                  <tr>
                    <td colSpan={6} className="py-6 text-center text-xs text-ink-500">
                      No roles granted yet — this table denies everyone by default.
                    </td>
                  </tr>
                ) : (
                  roles.map((role) => (
                    <tr key={role}>
                      <td className="py-2 pr-4 font-mono text-xs text-ink-200">{role}</td>
                      {ACTIONS.map((action) => (
                        <td key={action} className="px-2 py-2 text-center">
                          <input
                            type="checkbox"
                            className="accent-iris-500"
                            checked={matrix.get(role)?.has(action) ?? false}
                            onChange={(e) => setCell(role, action, e.target.checked)}
                          />
                        </td>
                      ))}
                      <td className="py-2 text-right">
                        <button
                          type="button"
                          className="text-xs text-ink-500 hover:text-coral-400"
                          onClick={() => removeRole(role)}
                        >
                          ✕
                        </button>
                      </td>
                    </tr>
                  ))
                )}
              </tbody>
            </table>
          </div>

          <div className="mt-4 flex gap-2">
            <input
              className="input-base flex-1 font-mono text-xs"
              placeholder='any · guests · users · user:<id> · team:<id>/<role> · label:<name>'
              value={newRole}
              onChange={(e) => setNewRole(e.target.value)}
              onKeyDown={(e) => e.key === "Enter" && (e.preventDefault(), addRole())}
            />
            <button type="button" className="btn-ghost border border-ink-700 text-xs" onClick={addRole}>
              + Add role
            </button>
          </div>
          {update.isPending ? <p className="mt-2 text-xs text-ink-500"><Spinner className="mr-1 inline size-3" />Saving…</p> : null}
        </section>

        <section className="surface border-coral-400/20 p-5">
          <h2 className="mb-3 text-sm font-medium text-coral-400">Danger zone</h2>
          <p className="mb-3 text-xs text-ink-500">
            Deleting <span className="font-mono text-ink-300">{table.data.name}</span> drops every
            column, index and row. Type its name to confirm.
          </p>
          {deleteTable.error instanceof ApiError ? (
            <div className="mb-3"><ErrorNote message={deleteTable.error.message} /></div>
          ) : null}
          <div className="flex gap-2">
            <input
              className="input-base flex-1"
              value={confirmName}
              onChange={(e) => setConfirmName(e.target.value)}
              placeholder={table.data.name}
            />
            <button
              type="button"
              className="btn-ghost shrink-0 border border-coral-400/60 text-coral-400 disabled:opacity-40"
              disabled={confirmName !== table.data.name || deleteTable.isPending}
              onClick={() => void onDeleteTable()}
            >
              {deleteTable.isPending ? <Spinner /> : "Delete table"}
            </button>
          </div>
        </section>
      </div>
    </div>
  );
}
