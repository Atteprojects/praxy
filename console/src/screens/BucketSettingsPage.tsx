import { useNavigate, useParams } from "@tanstack/react-router";
import { useState } from "react";
import { useTeams } from "../api/auth";
import { ApiError } from "../api/client";
import {
  useBucket, useBucketPermissions, useDeleteBucket, useUpdateBucket, useUpdateBucketPermissions,
} from "../api/storage";
import { AddRoleButton, RoleLabel } from "../components/RolePicker";
import { useToast } from "../components/toast";
import { ErrorNote, FullPageSpinner, Spinner, Toggle } from "../components/ui";
import { BucketDetailHeader } from "./BucketDetailHeader";
import { formatBytes } from "./storageFormat";

const ACTIONS = ["create", "read", "update", "delete"] as const;
type Action = (typeof ACTIONS)[number];

/** Same wire grammar and the same matrix shape as TableSettingsPage — bucket permissions *are* table permissions. */
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

export function BucketSettingsPage() {
  const { projectId, bucketId } = useParams({ strict: false }) as { projectId: string; bucketId: string };
  const navigate = useNavigate();
  const bucket = useBucket(projectId, bucketId);
  const permissions = useBucketPermissions(projectId, bucketId);
  const teams = useTeams(projectId);
  const updatePermissions = useUpdateBucketPermissions(projectId, bucketId);
  const updateBucket = useUpdateBucket(projectId, bucketId);
  const deleteBucket = useDeleteBucket(projectId);

  const [teamPickerOpen, setTeamPickerOpen] = useState(false);
  const [confirmName, setConfirmName] = useState("");
  const toast = useToast();
  const error = updatePermissions.error instanceof ApiError ? updatePermissions.error : null;

  if (bucket.isPending || permissions.isPending) return <FullPageSpinner />;
  if (bucket.isError) throw bucket.error;
  if (permissions.isError) throw permissions.error;

  const current = permissions.data.permissions;
  const matrix = parsePermissions(current);
  const roles = [...matrix.keys()];

  function applyPreset(preset: "public-read" | "signed-in-users") {
    const replaces = roles.length > 0 ? " Replaced the existing grants." : "";
    if (preset === "public-read") {
      updatePermissions.mutate(['read("any")'], {
        onSuccess: () => toast.success(`Anyone can now read files in this bucket.${replaces}`),
      });
    } else {
      updatePermissions.mutate(['read("users")', 'write("users")'], {
        onSuccess: () => toast.success(`Signed-in users can now read and write files.${replaces}`),
      });
    }
  }

  function applyTeamAccess(teamId: string) {
    const name = teams.data?.teams.find((t) => t.id === teamId)?.name ?? "the team";
    updatePermissions.mutate([`read("team:${teamId}")`, `write("team:${teamId}")`], {
      onSuccess: () => toast.success(`Full access granted to ${name}.`),
    });
    setTeamPickerOpen(false);
  }

  async function onDeleteBucket() {
    await deleteBucket.mutateAsync(bucketId);
    await navigate({ to: "/project/$projectId/storage", params: { projectId } });
  }

  return (
    <div>
      <BucketDetailHeader projectId={projectId} bucket={bucket.data} active="settings" />

      <div className="max-w-3xl space-y-8">
        <section className="surface p-5">
          <h2 className="mb-1 text-sm font-medium text-ink-100">Bucket</h2>
          <p className="mb-3 text-xs text-ink-500">
            A disabled bucket keeps its files but refuses every upload, rename and delete through the
            data-plane API. Reads through the console are unaffected.
          </p>
          <Toggle
            checked={bucket.data.enabled}
            onChange={(value) => updateBucket.mutate({ enabled: value })}
            label="Enabled"
          />
          <p className="mt-4 text-xs text-ink-500">
            Files up to <span className="font-medium text-ink-300">{formatBytes(bucket.data.maxFileSizeBytes)}</span>,{" "}
            {bucket.data.allowedMimeTypes == null ? (
              "any type accepted"
            ) : (
              <>
                accepting <span className="font-mono text-ink-300">{bucket.data.allowedMimeTypes.join(", ")}</span>
              </>
            )}
            .
          </p>
        </section>

        <section className="surface p-5">
          <div className="mb-4 flex items-center justify-between">
            <h2 className="text-sm font-medium text-ink-100">Permissions</h2>
            <div className="flex flex-wrap gap-2">
              <button type="button" className="btn-ghost border border-ink-700 text-xs" onClick={() => applyPreset("public-read")}>
                Public read
              </button>
              <button
                type="button"
                className="btn-ghost border border-ink-700 text-xs"
                onClick={() => applyPreset("signed-in-users")}
              >
                Signed-in users
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
                <tr className="border-b border-ink-800 text-xs uppercase text-ink-500">
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
                      No roles granted yet — this bucket denies everyone by default.
                    </td>
                  </tr>
                ) : (
                  roles.map((role) => (
                    <tr key={role}>
                      <td className="py-2 pr-4">
                        <RoleLabel projectId={projectId} role={role} />
                      </td>
                      {ACTIONS.map((action) => (
                        <td key={action} className="px-2 py-2 text-center">
                          <input
                            type="checkbox"
                            className="accent-iris-500"
                            checked={matrix.get(role)?.has(action) ?? false}
                            onChange={(e) =>
                              updatePermissions.mutate(withPermission(current, action, role, e.target.checked))
                            }
                          />
                        </td>
                      ))}
                      <td className="py-2 text-right">
                        <button
                          type="button"
                          className="text-xs text-ink-500 hover:text-coral-400"
                          onClick={() => updatePermissions.mutate(current.filter((p) => !p.endsWith(`("${role}")`)))}
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

          <div className="mt-2 text-[11px] text-ink-500">
            <span className="font-mono">create</span> uploads · <span className="font-mono">read</span> lists and
            downloads · <span className="font-mono">update</span> renames · <span className="font-mono">delete</span>{" "}
            removes. Operators reach every bucket from this console regardless.
          </div>

          <div className="mt-4 flex justify-end">
            <AddRoleButton
              projectId={projectId}
              existingRoles={roles}
              onPick={(role) => {
                if (matrix.has(role)) return;
                // A new role starts with read only — the matrix is where you widen it.
                updatePermissions.mutate([...current, `read("${role}")`]);
              }}
            />
          </div>
          {updatePermissions.isPending ? (
            <p className="mt-2 text-xs text-ink-500"><Spinner className="mr-1 inline size-3" />Saving…</p>
          ) : null}
        </section>

        <section className="surface border-coral-400/20 p-5">
          <h2 className="mb-3 text-sm font-medium text-coral-400">Danger zone</h2>
          <p className="mb-3 text-xs text-ink-500">
            Deleting <span className="font-mono text-ink-300">{bucket.data.name}</span> deletes every file in it and
            the bytes behind them. Type its name to confirm.
          </p>
          {deleteBucket.error instanceof ApiError ? (
            <div className="mb-3"><ErrorNote message={deleteBucket.error.message} /></div>
          ) : null}
          <div className="flex gap-2">
            <input
              className="input-base flex-1"
              value={confirmName}
              onChange={(e) => setConfirmName(e.target.value)}
              placeholder={bucket.data.name}
            />
            <button
              type="button"
              className="btn-ghost shrink-0 border border-coral-400/60 text-coral-400 disabled:opacity-40"
              disabled={confirmName !== bucket.data.name || deleteBucket.isPending}
              onClick={() => void onDeleteBucket()}
            >
              {deleteBucket.isPending ? <Spinner /> : "Delete bucket"}
            </button>
          </div>
        </section>
      </div>
    </div>
  );
}
