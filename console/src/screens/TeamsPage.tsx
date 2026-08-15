import { Link, useNavigate, useParams } from "@tanstack/react-router";
import { useState, type FormEvent } from "react";
import {
  useAddTeamMember, useCreateTeam, useDeleteTeam, useRemoveTeamMember, useTeam, useTeamMemberships,
  useTeams,
} from "../api/auth";
import { ApiError } from "../api/client";
import {
  Badge, DataTable, EmptyState, ErrorNote, Field, FullPageSpinner, IdChip, Modal, Spinner, timeAgo,
} from "../components/ui";
import { STR } from "../strings";

const HEADERS = ["Team", "ID", "Members", "Created"];

export function TeamsPage() {
  const { projectId } = useParams({ strict: false }) as { projectId: string };
  const teams = useTeams(projectId);
  const [creating, setCreating] = useState(false);

  if (teams.isPending) return <FullPageSpinner />;
  if (teams.isError) throw teams.error;

  return (
    <div>
      <div className="mb-6 flex items-center justify-between">
        <h1 className="text-2xl font-semibold tracking-tight">{STR.teams}</h1>
        <button type="button" className="btn-primary" onClick={() => setCreating(true)}>
          + Create team
        </button>
      </div>

      {creating ? <CreateTeamModal projectId={projectId} onClose={() => setCreating(false)} /> : null}

      {teams.data.total === 0 ? (
        <EmptyState
          headers={HEADERS}
          title="No teams yet. Teams group users and power team:<id> permissions."
          action={
            <button type="button" className="btn-primary" onClick={() => setCreating(true)}>
              + Create team
            </button>
          }
        />
      ) : (
        <DataTable headers={HEADERS}>
          {teams.data.teams.map((team) => (
            <tr
              key={team.id}
              className="cursor-pointer transition-colors hover:bg-ink-850/60"
              onClick={() => undefined}
            >
              <td className="px-4 py-3">
                <Link
                  to="/project/$projectId/auth/teams/$teamId"
                  params={{ projectId, teamId: team.id }}
                  className="block font-medium text-ink-100 hover:text-iris-300"
                >
                  {team.name}
                </Link>
              </td>
              <td className="px-4 py-3">
                <IdChip id={team.id} />
              </td>
              <td className="px-4 py-3 text-ink-400">{team.memberCount}</td>
              <td className="px-4 py-3 whitespace-nowrap text-ink-400">
                {new Date(team.createdAt).toLocaleDateString()}
              </td>
            </tr>
          ))}
        </DataTable>
      )}
    </div>
  );
}

function CreateTeamModal({ projectId, onClose }: { projectId: string; onClose: () => void }) {
  const create = useCreateTeam(projectId);
  const navigate = useNavigate();
  const [name, setName] = useState("");
  const error = create.error instanceof ApiError ? create.error : null;

  async function onSubmit(e: FormEvent) {
    e.preventDefault();
    const team = await create.mutateAsync({ name });
    onClose();
    await navigate({
      to: "/project/$projectId/auth/teams/$teamId",
      params: { projectId, teamId: team.id },
    });
  }

  return (
    <Modal title="Create team" onClose={onClose}>
      <form onSubmit={(e) => void onSubmit(e)} className="space-y-4">
        {error && !error.envelope.fields ? <ErrorNote message={error.message} /> : null}
        <Field label="Name" error={error?.fieldErrors("name")[0]}>
          <input
            className="input-base"
            required
            autoFocus
            value={name}
            onChange={(e) => setName(e.target.value)}
            placeholder="Engineering"
          />
        </Field>
        <button type="submit" className="btn-primary w-full" disabled={create.isPending}>
          {create.isPending ? <Spinner /> : "Create team"}
        </button>
      </form>
    </Modal>
  );
}

export function TeamDetailPage() {
  const { projectId, teamId } = useParams({ strict: false }) as { projectId: string; teamId: string };
  const navigate = useNavigate();
  const team = useTeam(projectId, teamId);
  const memberships = useTeamMemberships(projectId, teamId);
  const addMember = useAddTeamMember(projectId, teamId);
  const removeMember = useRemoveTeamMember(projectId, teamId);
  const deleteTeam = useDeleteTeam(projectId);
  const [email, setEmail] = useState("");
  const [roles, setRoles] = useState("");
  const [confirmingDelete, setConfirmingDelete] = useState(false);

  if (team.isPending || memberships.isPending) return <FullPageSpinner />;
  if (team.isError) throw team.error;
  if (memberships.isError) throw memberships.error;

  const addError = addMember.error instanceof ApiError ? addMember.error : null;

  async function onAdd(e: FormEvent) {
    e.preventDefault();
    await addMember.mutateAsync({
      email,
      roles: roles.split(",").map((role) => role.trim()).filter(Boolean),
    });
    setEmail("");
    setRoles("");
  }

  return (
    <div>
      <Link to="/project/$projectId/auth/teams" params={{ projectId }} className="btn-ghost mb-4 -ml-3 text-xs">
        ← {STR.teams}
      </Link>

      <div className="mb-6 flex flex-wrap items-center gap-3">
        <h1 className="text-2xl font-semibold tracking-tight">{team.data.name}</h1>
        <IdChip id={team.data.id} />
      </div>

      <div className="mb-6 max-w-2xl surface p-5">
        <h2 className="mb-3 text-sm font-medium uppercase tracking-wide text-ink-500">Add member</h2>
        <form onSubmit={(e) => void onAdd(e)} className="flex flex-wrap items-start gap-2">
          <input
            className="input-base flex-1 min-w-48"
            type="email"
            required
            placeholder="user@example.com — created if unknown"
            value={email}
            onChange={(e) => setEmail(e.target.value)}
          />
          <input
            className="input-base w-44"
            placeholder="roles, e.g. owner"
            value={roles}
            onChange={(e) => setRoles(e.target.value)}
          />
          <button type="submit" className="btn-primary" disabled={addMember.isPending}>
            {addMember.isPending ? <Spinner /> : "Add"}
          </button>
        </form>
        {addError ? <p className="mt-2 text-xs text-coral-400">{addError.message}</p> : null}
        <p className="mt-2 text-xs text-ink-500">
          Console adds are immediate. In-app invitations (with the acceptance email) go through the
          client API with a session.
        </p>
      </div>

      {memberships.data.total === 0 ? (
        <p className="py-8 text-center text-sm text-ink-500">No members yet.</p>
      ) : (
        <DataTable headers={["Member", "Roles", "Status", "Joined", ""]}>
          {memberships.data.memberships.map((membership) => (
            <tr key={membership.id}>
              <td className="px-4 py-3">
                <Link
                  to="/project/$projectId/auth/users/$userId"
                  params={{ projectId, userId: membership.userId }}
                  className="block"
                >
                  <span className="block truncate font-medium text-ink-100 hover:text-iris-300">
                    {membership.userEmail}
                  </span>
                  <span className="block truncate text-xs text-ink-500">{membership.userName || "—"}</span>
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
              <td className="px-4 py-3 text-right">
                <button
                  type="button"
                  className="btn-ghost border border-ink-700 px-2 py-1 text-xs text-coral-400"
                  disabled={removeMember.isPending}
                  onClick={() => removeMember.mutate(membership.id)}
                >
                  Remove
                </button>
              </td>
            </tr>
          ))}
        </DataTable>
      )}

      <div className="mt-8 max-w-2xl surface border-coral-400/20 p-5">
        <h2 className="mb-3 text-sm font-medium uppercase tracking-wide text-coral-400">Danger zone</h2>
        <button
          type="button"
          className={`btn-ghost border ${confirmingDelete ? "border-coral-400 text-coral-400" : "border-ink-700"}`}
          disabled={deleteTeam.isPending}
          onClick={async () => {
            if (!confirmingDelete) {
              setConfirmingDelete(true);
              setTimeout(() => setConfirmingDelete(false), 3000);
              return;
            }
            await deleteTeam.mutateAsync(teamId);
            await navigate({ to: "/project/$projectId/auth/teams", params: { projectId } });
          }}
        >
          {confirmingDelete ? "Click again to delete the team" : "Delete team"}
        </button>
      </div>
    </div>
  );
}
