import { Link, useNavigate, useParams } from "@tanstack/react-router";
import { useState, type FormEvent } from "react";
import { useCreateUser, useProjectUsers } from "../api/auth";
import { ApiError } from "../api/client";
import {
  Badge, DataTable, EmptyState, ErrorNote, Field, FullPageSpinner, IdChip, Modal, PageHeader, Spinner, timeAgo,
} from "../components/ui";
import { STR } from "../strings";

const HEADERS = ["User", "ID", "Status", "Labels", "Joined", "Last activity"];

export function UsersPage() {
  const { projectId } = useParams({ strict: false }) as { projectId: string };
  const [search, setSearch] = useState("");
  const [creating, setCreating] = useState(false);
  const users = useProjectUsers(projectId, search);

  if (users.isPending) return <FullPageSpinner />;
  if (users.isError) throw users.error;

  return (
    <div>
      <PageHeader
        title={STR.users}
        description="Everyone who can sign in to this project's apps. Labels here become label:<name> permission roles."
        actions={
          <>
            <input
              className="input-base w-56"
              placeholder="Search email or name…"
              value={search}
              onChange={(e) => setSearch(e.target.value)}
            />
            <button type="button" className="btn-primary" onClick={() => setCreating(true)}>
              + Create user
            </button>
          </>
        }
      />

      {creating ? <CreateUserModal projectId={projectId} onClose={() => setCreating(false)} /> : null}

      {users.data.total === 0 ? (
        <EmptyState
          headers={HEADERS}
          title={search ? "No users match your search." : "No users yet — create the first one."}
          action={
            search ? (
              <button type="button" className="btn-ghost border border-ink-700" onClick={() => setSearch("")}>
                Clear search
              </button>
            ) : (
              <button type="button" className="btn-primary" onClick={() => setCreating(true)}>
                + Create user
              </button>
            )
          }
        />
      ) : (
        <>
          <DataTable headers={HEADERS}>
            {users.data.users.map(({ user, lastActivityAt }) => (
              <UserRow key={user.id} projectId={projectId} user={user} lastActivityAt={lastActivityAt} />
            ))}
          </DataTable>
          <p className="mt-3 text-xs text-ink-500">{users.data.total} total</p>
        </>
      )}
    </div>
  );
}

function UserRow({
  projectId,
  user,
  lastActivityAt,
}: {
  projectId: string;
  user: import("../api/types").AppUser;
  lastActivityAt: string | null;
}) {
  const navigate = useNavigate();
  return (
    <tr
      className="cursor-pointer transition-colors hover:bg-ink-850/60"
      onClick={() =>
        void navigate({
          to: "/project/$projectId/auth/users/$userId",
          params: { projectId, userId: user.id },
        })
      }
    >
      <td className="px-4 py-3">
        <Link
          to="/project/$projectId/auth/users/$userId"
          params={{ projectId, userId: user.id }}
          className="block"
          onClick={(e) => e.stopPropagation()}
        >
          <span className="block truncate font-medium text-ink-100">{user.email}</span>
          <span className="block truncate text-xs text-ink-500">{user.name || "—"}</span>
        </Link>
      </td>
      <td className="px-4 py-3" onClick={(e) => e.stopPropagation()}>
        <IdChip id={user.id} />
      </td>
      <td className="px-4 py-3">
        <span className="flex gap-1.5">
          {user.status ? <Badge tone="mint">active</Badge> : <Badge tone="coral">blocked</Badge>}
          {user.emailVerified ? <Badge tone="iris">verified</Badge> : null}
        </span>
      </td>
      <td className="px-4 py-3">
        {user.labels.length === 0 ? (
          <span className="text-ink-700">—</span>
        ) : (
          <span className="flex flex-wrap gap-1">
            {user.labels.map((label) => (
              <Badge key={label}>{label}</Badge>
            ))}
          </span>
        )}
      </td>
      <td className="px-4 py-3 whitespace-nowrap text-ink-400">
        {new Date(user.createdAt).toLocaleDateString()}
      </td>
      <td className="px-4 py-3 whitespace-nowrap text-ink-400">{timeAgo(lastActivityAt)}</td>
    </tr>
  );
}

function CreateUserModal({ projectId, onClose }: { projectId: string; onClose: () => void }) {
  const create = useCreateUser(projectId);
  const navigate = useNavigate();
  const [email, setEmail] = useState("");
  const [name, setName] = useState("");
  const [password, setPassword] = useState("");
  const error = create.error instanceof ApiError ? create.error : null;

  async function onSubmit(e: FormEvent) {
    e.preventDefault();
    const user = await create.mutateAsync({
      email,
      name: name || undefined,
      password: password || undefined,
    });
    onClose();
    await navigate({
      to: "/project/$projectId/auth/users/$userId",
      params: { projectId, userId: user.id },
    });
  }

  return (
    <Modal title="Create user" onClose={onClose}>
      <form onSubmit={(e) => void onSubmit(e)} className="space-y-4">
        {error && !error.envelope.fields ? <ErrorNote message={error.message} /> : null}
        <Field label="Email" error={error?.fieldErrors("email")[0]}>
          <input
            className="input-base"
            type="email"
            required
            autoFocus
            value={email}
            onChange={(e) => setEmail(e.target.value)}
            placeholder="user@example.com"
          />
        </Field>
        <Field label="Name (optional)" error={error?.fieldErrors("name")[0]}>
          <input className="input-base" value={name} onChange={(e) => setName(e.target.value)} />
        </Field>
        <Field label="Password (optional — leave empty for OAuth-only)" error={error?.fieldErrors("password")[0]}>
          <input
            className="input-base"
            type="password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            placeholder="At least 8 characters"
          />
        </Field>
        <button type="submit" className="btn-primary w-full" disabled={create.isPending}>
          {create.isPending ? <Spinner /> : "Create user"}
        </button>
      </form>
    </Modal>
  );
}
