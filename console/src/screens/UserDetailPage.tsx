import { Link, useNavigate, useParams } from "@tanstack/react-router";
import { useState } from "react";
import {
  useDeleteUser, useRevokeSession, useUpdateUserLabels, useUpdateUserStatus, useUser,
  useUserMemberships, useUserSessions,
} from "../api/auth";
import {
  Badge, DataTable, FullPageSpinner, IdChip, Spinner, Tabs, timeAgo,
} from "../components/ui";
import { STR } from "../strings";

const TABS = [
  { id: "overview", label: "Overview" },
  { id: "sessions", label: "Sessions" },
  { id: "memberships", label: "Memberships" },
] as const;

type TabId = (typeof TABS)[number]["id"];

export function UserDetailPage() {
  const { projectId, userId } = useParams({ strict: false }) as { projectId: string; userId: string };
  const detail = useUser(projectId, userId);
  const [tab, setTab] = useState<TabId>("overview");

  if (detail.isPending) return <FullPageSpinner />;
  if (detail.isError) throw detail.error;

  const { user, identities } = detail.data;

  return (
    <div>
      <Link
        to="/project/$projectId/auth/users"
        params={{ projectId }}
        className="btn-ghost mb-4 -ml-3 text-xs"
      >
        ← {STR.users}
      </Link>

      <div className="mb-6 flex flex-wrap items-center gap-3">
        <h1 className="text-2xl font-semibold tracking-tight">{user.name || user.email}</h1>
        {user.status ? <Badge tone="mint">active</Badge> : <Badge tone="coral">blocked</Badge>}
        {user.emailVerified ? <Badge tone="iris">verified</Badge> : <Badge>unverified</Badge>}
        <IdChip id={user.id} />
      </div>

      <Tabs tabs={TABS} active={tab} onSelect={setTab} />

      {tab === "overview" ? <OverviewTab projectId={projectId} user={user} identities={identities} /> : null}
      {tab === "sessions" ? <SessionsTab projectId={projectId} userId={userId} /> : null}
      {tab === "memberships" ? <MembershipsTab projectId={projectId} userId={userId} /> : null}
    </div>
  );
}

function OverviewTab({
  projectId,
  user,
  identities,
}: {
  projectId: string;
  user: import("../api/types").AppUser;
  identities: import("../api/types").UserIdentity[];
}) {
  const navigate = useNavigate();
  const updateStatus = useUpdateUserStatus(projectId, user.id);
  const updateLabels = useUpdateUserLabels(projectId, user.id);
  const deleteUser = useDeleteUser(projectId);
  const [labelDraft, setLabelDraft] = useState(user.labels.join(", "));
  const [confirmingDelete, setConfirmingDelete] = useState(false);

  return (
    <div className="max-w-2xl space-y-6">
      <div className="surface p-5">
        <h2 className="mb-4 text-sm font-medium uppercase tracking-wide text-ink-500">Profile</h2>
        <dl className="grid grid-cols-1 gap-3 text-sm sm:grid-cols-2">
          <Item label="Email" value={user.email} />
          <Item label="Name" value={user.name || "—"} />
          <Item label="Joined" value={new Date(user.createdAt).toLocaleString()} />
          <Item label="Updated" value={new Date(user.updatedAt).toLocaleString()} />
        </dl>
      </div>

      <div className="surface p-5">
        <h2 className="mb-4 text-sm font-medium uppercase tracking-wide text-ink-500">
          Labels <span className="normal-case text-ink-700">— become label:&lt;x&gt; permission roles</span>
        </h2>
        <div className="flex gap-2">
          <input
            className="input-base"
            value={labelDraft}
            onChange={(e) => setLabelDraft(e.target.value)}
            placeholder="vip, beta (comma-separated, alphanumeric)"
          />
          <button
            type="button"
            className="btn-ghost shrink-0 border border-ink-700"
            disabled={updateLabels.isPending}
            onClick={() =>
              updateLabels.mutate(
                labelDraft.split(",").map((label) => label.trim()).filter(Boolean),
              )
            }
          >
            {updateLabels.isPending ? <Spinner /> : "Save"}
          </button>
        </div>
        {updateLabels.isError ? (
          <p className="mt-2 text-xs text-coral-400">{updateLabels.error.message}</p>
        ) : null}
      </div>

      {identities.length > 0 ? (
        <div className="surface p-5">
          <h2 className="mb-4 text-sm font-medium uppercase tracking-wide text-ink-500">Identities</h2>
          <ul className="space-y-2 text-sm">
            {identities.map((identity) => (
              <li key={identity.id} className="flex items-center gap-3">
                <Badge tone="iris">{identity.provider}</Badge>
                <span className="text-ink-300">{identity.providerEmail ?? identity.providerUid}</span>
                <span className="text-xs text-ink-500">linked {timeAgo(identity.createdAt)}</span>
              </li>
            ))}
          </ul>
        </div>
      ) : null}

      <div className="surface border-coral-400/20 p-5">
        <h2 className="mb-4 text-sm font-medium uppercase tracking-wide text-coral-400">Danger zone</h2>
        <div className="flex flex-wrap gap-3">
          <button
            type="button"
            className="btn-ghost border border-ink-700"
            disabled={updateStatus.isPending}
            onClick={() => updateStatus.mutate(!user.status)}
          >
            {user.status ? "Block user" : "Unblock user"}
          </button>
          <button
            type="button"
            className={`btn-ghost border ${confirmingDelete ? "border-coral-400 text-coral-400" : "border-ink-700"}`}
            disabled={deleteUser.isPending}
            onClick={async () => {
              if (!confirmingDelete) {
                setConfirmingDelete(true);
                setTimeout(() => setConfirmingDelete(false), 3000);
                return;
              }
              await deleteUser.mutateAsync(user.id);
              await navigate({ to: "/project/$projectId/auth/users", params: { projectId } });
            }}
          >
            {confirmingDelete ? "Click again to delete permanently" : "Delete user"}
          </button>
        </div>
        <p className="mt-3 text-xs text-ink-500">
          Blocking revokes access immediately; deleting removes the user, their sessions and memberships.
        </p>
      </div>
    </div>
  );
}

function Item({ label, value }: { label: string; value: string }) {
  return (
    <div>
      <dt className="text-xs uppercase tracking-wide text-ink-500">{label}</dt>
      <dd className="mt-0.5 text-ink-100">{value}</dd>
    </div>
  );
}

function SessionsTab({ projectId, userId }: { projectId: string; userId: string }) {
  const sessions = useUserSessions(projectId, userId);
  const revoke = useRevokeSession(projectId, userId);

  if (sessions.isPending) return <FullPageSpinner />;
  if (sessions.isError) throw sessions.error;

  const headers = ["Provider", "IP", "Client", "Created", "Expires", ""];

  return (
    <div>
      {sessions.data.total === 0 ? (
        <p className="py-12 text-center text-sm text-ink-500">
          No active sessions. They appear here the moment this user signs in.
        </p>
      ) : (
        <>
          <div className="mb-3 flex justify-end">
            <button
              type="button"
              className="btn-ghost border border-ink-700 text-xs"
              disabled={revoke.isPending}
              onClick={() => revoke.mutate("all")}
            >
              Revoke all sessions
            </button>
          </div>
          <DataTable headers={headers}>
            {sessions.data.sessions.map((session) => (
              <tr key={session.id}>
                <td className="px-4 py-3">
                  <Badge tone="iris">{session.provider}</Badge>
                </td>
                <td className="px-4 py-3 font-mono text-xs text-ink-400">{session.ip ?? "—"}</td>
                <td className="max-w-56 truncate px-4 py-3 text-xs text-ink-400" title={session.userAgent ?? undefined}>
                  {session.userAgent ?? "—"}
                </td>
                <td className="px-4 py-3 whitespace-nowrap text-ink-400">{timeAgo(session.createdAt)}</td>
                <td className="px-4 py-3 whitespace-nowrap text-ink-400">
                  {new Date(session.expiresAt).toLocaleDateString()}
                </td>
                <td className="px-4 py-3 text-right">
                  <button
                    type="button"
                    className="btn-ghost border border-ink-700 px-2 py-1 text-xs text-coral-400"
                    disabled={revoke.isPending}
                    onClick={() => revoke.mutate(session.id)}
                  >
                    Revoke
                  </button>
                </td>
              </tr>
            ))}
          </DataTable>
          <p className="mt-3 text-xs text-ink-500">
            Revoking a session returns 401 to that client on its next request.
          </p>
        </>
      )}
    </div>
  );
}

function MembershipsTab({ projectId, userId }: { projectId: string; userId: string }) {
  const memberships = useUserMemberships(projectId, userId);

  if (memberships.isPending) return <FullPageSpinner />;
  if (memberships.isError) throw memberships.error;

  return memberships.data.total === 0 ? (
    <p className="py-12 text-center text-sm text-ink-500">Not a member of any team.</p>
  ) : (
    <DataTable headers={["Team", "Roles", "Status", "Joined"]}>
      {memberships.data.memberships.map(({ membership, teamName }) => (
        <tr key={membership.id}>
          <td className="px-4 py-3">
            <Link
              to="/project/$projectId/auth/teams/$teamId"
              params={{ projectId, teamId: membership.teamId }}
              className="font-medium text-ink-100 hover:text-iris-300"
            >
              {teamName}
            </Link>
          </td>
          <td className="px-4 py-3">
            <span className="flex flex-wrap gap-1">
              {membership.roles.length === 0 ? (
                <span className="text-ink-700">—</span>
              ) : (
                membership.roles.map((role) => <Badge key={role}>{role}</Badge>)
              )}
            </span>
          </td>
          <td className="px-4 py-3">
            {membership.confirmed ? <Badge tone="mint">member</Badge> : <Badge tone="amber">invited</Badge>}
          </td>
          <td className="px-4 py-3 whitespace-nowrap text-ink-400">{timeAgo(membership.joinedAt)}</td>
        </tr>
      ))}
    </DataTable>
  );
}
