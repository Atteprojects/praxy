import { Link, useNavigate, useParams } from "@tanstack/react-router";
import { useState } from "react";
import {
  useDeleteUser, usePlatforms, useResetUserPassword, useRevokeSession, useSendUserVerification,
  useUpdateUserEmail, useUpdateUserLabels, useUpdateUserName, useUpdateUserStatus,
  useUpdateUserVerification, useUser, useUserMemberships, useUserSessions,
} from "../api/auth";
import { ApiError } from "../api/client";
import { ConfirmButton } from "../components/ConfirmButton";
import { useToast } from "../components/toast";
import {
  Badge, DataTable, ErrorNote, Field, FullPageSpinner, IdChip, PageHeader, Spinner, Tabs, timeAgo,
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

      <PageHeader
        title={user.name || user.email}
        chips={
          <>
            {user.status ? <Badge tone="mint">active</Badge> : <Badge tone="coral">blocked</Badge>}
            {user.emailVerified ? <Badge tone="iris">verified</Badge> : <Badge>unverified</Badge>}
            <IdChip id={user.id} />
          </>
        }
        tabs={<Tabs tabs={TABS} active={tab} onSelect={setTab} />}
      />

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
  const toast = useToast();

  return (
    <div className="max-w-2xl space-y-6">
      <ProfileCard projectId={projectId} user={user} />
      <VerificationCard projectId={projectId} user={user} />
      <PasswordCard projectId={projectId} user={user} />

      <div className="surface p-5">
        <h2 className="mb-4 text-sm font-medium text-ink-100">
          Labels <span className="text-ink-700">— become label:&lt;x&gt; permission roles</span>
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
                { onSuccess: () => toast.success("Labels saved.") },
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
          <h2 className="mb-4 text-sm font-medium text-ink-100">Identities</h2>
          <ul className="space-y-2 text-sm">
            {identities.map((identity) => (
              <li key={identity.id} className="flex items-center gap-3">
                <Badge tone="iris">{identity.provider}</Badge>
                <span className="text-ink-300">{identity.providerEmail ?? identity.providerUid}</span>
                <span className="text-xs text-ink-500">linked {timeAgo(identity.createdAt)}</span>
              </li>
            ))}
          </ul>
          <p className="mt-3 text-xs text-ink-500">
            A provider's own email is a separate fact from the account address above — changing one
            does not change the other, and sign-in through the provider keeps working either way.
          </p>
        </div>
      ) : null}

      <section className="surface border-coral-400/20 p-5">
        <h2 className="mb-4 text-sm font-medium text-coral-400">Danger zone</h2>
        <div className="flex flex-wrap gap-3">
          <ConfirmButton
            label={user.status ? "Block user" : "Unblock user"}
            title={user.status ? "Block user?" : "Unblock user?"}
            confirmLabel={user.status ? "Block user" : "Unblock user"}
            successMessage={user.status ? "User blocked." : "User unblocked."}
            className="btn-ghost border border-ink-700"
            body={
              user.status ? (
                <>
                  <span className="font-mono text-ink-300">{user.email}</span> can no longer sign in, and existing
                  sessions stop working. Their data is kept, and you can unblock them at any time.
                </>
              ) : (
                <>
                  <span className="font-mono text-ink-300">{user.email}</span> can sign in again. They will need to
                  create a new session.
                </>
              )
            }
            onConfirm={() => updateStatus.mutateAsync(!user.status)}
          />
          <ConfirmButton
            label="Delete user"
            title="Delete user permanently?"
            confirmLabel="Delete permanently"
            successMessage={`Deleted ${user.email}.`}
            className="btn-ghost border border-ink-700 text-coral-400"
            body={
              <>
                <span className="font-mono text-ink-300">{user.email}</span>, their sessions, identities and team
                memberships are removed. Rows they own are not deleted, but any{" "}
                <span className="font-mono text-ink-300">user:{user.id}</span> grant stops resolving. This cannot
                be undone.
              </>
            }
            onConfirm={async () => {
              await deleteUser.mutateAsync(user.id);
              await navigate({ to: "/project/$projectId/auth/users", params: { projectId } });
            }}
          />
        </div>
        <p className="mt-3 text-xs text-ink-500">
          Blocking revokes access immediately; deleting removes the user, their sessions and memberships.
        </p>
      </section>
    </div>
  );
}

type AppUser = import("../api/types").AppUser;

/**
 * Email and name, both editable in place. Everything destructive-ish about the email lives in the
 * confirm dialog rather than in a note underneath it — the operator has to read what resets before
 * the change happens, not after.
 */
function ProfileCard({ projectId, user }: { projectId: string; user: AppUser }) {
  const updateEmail = useUpdateUserEmail(projectId, user.id);
  const updateName = useUpdateUserName(projectId, user.id);
  const [emailDraft, setEmailDraft] = useState(user.email);
  const [nameDraft, setNameDraft] = useState(user.name);
  const toast = useToast();

  const emailChanged = emailDraft.trim().toLowerCase() !== user.email;
  const nameChanged = nameDraft.trim() !== user.name;

  return (
    <div className="surface p-5">
      <h2 className="mb-4 text-sm font-medium text-ink-100">Profile</h2>

      <div className="space-y-4">
        <Field label="Email" error={fieldError(updateEmail.error, "email")}>
          <div className="flex gap-2">
            <input
              className="input-base"
              type="email"
              value={emailDraft}
              onChange={(e) => setEmailDraft(e.target.value)}
              placeholder="ada@example.com"
            />
            <ConfirmButton
              label="Change"
              title="Change this user's email?"
              confirmLabel="Change email"
              successMessage="Email changed."
              disabled={!emailChanged}
              className="btn-ghost shrink-0 border border-ink-700 disabled:opacity-40"
              body={
                <div className="space-y-3">
                  <p>
                    <span className="font-mono text-ink-300">{user.email}</span> becomes{" "}
                    <span className="font-mono text-ink-300">{emailDraft.trim().toLowerCase()}</span>. They sign
                    in with the new address from now on; the old one stops working immediately.
                  </p>
                  <p>
                    The account is marked <span className="text-ink-300">unverified</span> — nobody has proved
                    they own the new address yet, so the{" "}
                    <span className="font-mono text-ink-300">users/verified</span> permission role stops
                    resolving for them until they verify it.
                  </p>
                  <p>Existing sessions are not revoked; they stay signed in.</p>
                </div>
              }
              onConfirm={() => updateEmail.mutateAsync(emailDraft.trim())}
            />
          </div>
        </Field>

        <Field label="Name" error={fieldError(updateName.error, "name")}>
          <div className="flex gap-2">
            <input
              className="input-base"
              value={nameDraft}
              onChange={(e) => setNameDraft(e.target.value)}
              placeholder="Ada Lovelace"
            />
            <button
              type="button"
              className="btn-ghost shrink-0 border border-ink-700 disabled:opacity-40"
              disabled={!nameChanged || updateName.isPending}
              onClick={() =>
                updateName.mutate(nameDraft.trim(), { onSuccess: () => toast.success("Name saved.") })
              }
            >
              {updateName.isPending ? <Spinner /> : "Save"}
            </button>
          </div>
        </Field>
      </div>

      <dl className="mt-5 grid grid-cols-1 gap-3 border-t border-ink-800 pt-4 text-sm sm:grid-cols-2">
        <Item label="Joined" value={new Date(user.createdAt).toLocaleString()} />
        <Item label="Updated" value={new Date(user.updatedAt).toLocaleString()} />
      </dl>
    </div>
  );
}

/**
 * Verified-ness two ways: settle it directly, or send the user the mail again. Setting it by hand
 * is the escape hatch for an address that works but whose owner can never complete the round-trip;
 * it grants a permission role, so it asks first.
 */
function VerificationCard({ projectId, user }: { projectId: string; user: AppUser }) {
  const update = useUpdateUserVerification(projectId, user.id);
  const send = useSendUserVerification(projectId, user.id);
  const platforms = usePlatforms(projectId);
  const [url, setUrl] = useState(() => localStorage.getItem(verifyUrlKey(projectId)) ?? "");
  const toast = useToast();

  // The server checks the URL against these; showing them turns a rejection into a fix.
  const hostnames = (platforms.data?.platforms ?? [])
    .map((platform) => platform.hostname)
    .filter((hostname): hostname is string => hostname !== null);

  return (
    <div className="surface p-5">
      <h2 className="mb-4 text-sm font-medium text-ink-100">
        Email verification{" "}
        <span className="text-ink-700">— grants the users/verified permission role</span>
      </h2>

      <div className="flex flex-wrap items-center gap-3">
        {user.emailVerified ? <Badge tone="iris">verified</Badge> : <Badge>unverified</Badge>}
        <ConfirmButton
          label={user.emailVerified ? "Mark unverified" : "Mark verified"}
          title={user.emailVerified ? "Mark this address unverified?" : "Mark this address verified?"}
          confirmLabel={user.emailVerified ? "Mark unverified" : "Mark verified"}
          successMessage={user.emailVerified ? "Marked unverified." : "Marked verified."}
          className="btn-ghost border border-ink-700"
          body={
            user.emailVerified ? (
              <>
                <span className="font-mono text-ink-300">{user.email}</span> goes back to unverified.{" "}
                <span className="font-mono text-ink-300">users/verified</span> stops resolving for them, so
                any table or function granted to that role becomes unreachable.
              </>
            ) : (
              <>
                You are asserting that <span className="font-mono text-ink-300">{user.email}</span> reaches
                this person, without them proving it. They gain the{" "}
                <span className="font-mono text-ink-300">users/verified</span> role and everything granted to
                it. Prefer sending the mail below when the address actually works.
              </>
            )
          }
          onConfirm={() => update.mutateAsync(!user.emailVerified)}
        />
      </div>

      {user.emailVerified ? null : (
        <div className="mt-5 border-t border-ink-800 pt-4">
          <Field label="Send the verification email again" error={fieldError(send.error, "url")}>
            <div className="flex gap-2">
              <input
                className="input-base"
                value={url}
                onChange={(e) => setUrl(e.target.value)}
                placeholder="https://app.example.com/verify"
              />
              <button
                type="button"
                className="btn-ghost shrink-0 border border-ink-700 disabled:opacity-40"
                disabled={!url.trim() || send.isPending}
                onClick={() =>
                  send.mutate(url.trim(), {
                    onSuccess: () => {
                      localStorage.setItem(verifyUrlKey(projectId), url.trim());
                      toast.success(`Verification email sent to ${user.email}.`);
                    },
                  })
                }
              >
                {send.isPending ? <Spinner /> : "Send"}
              </button>
            </div>
          </Field>
          <p className="mt-2 text-xs text-ink-500">
            Where your app handles verification — Praxy appends{" "}
            <span className="font-mono">?userId=&amp;secret=</span> and mails the link. The hostname must be
            a registered platform:{" "}
            {hostnames.length > 0 ? (
              <span className="font-mono text-ink-400">{hostnames.join(", ")}</span>
            ) : (
              <Link
                to="/project/$projectId/platforms"
                params={{ projectId }}
                className="text-iris-300 hover:underline"
              >
                none registered yet
              </Link>
            )}
            .
          </p>
        </div>
      )}
      {send.error && !fieldError(send.error, "url") ? (
        <div className="mt-3">
          <ErrorNote message={send.error.message} />
        </div>
      ) : null}
    </div>
  );
}

/**
 * Operator-set password. Reveal-once, like an API key: the value is shown here and nowhere else,
 * because only its hash is stored. Setting it ends every live session — say so before the click,
 * not in a toast afterwards.
 */
function PasswordCard({ projectId, user }: { projectId: string; user: AppUser }) {
  const reset = useResetUserPassword(projectId, user.id);
  const [draft, setDraft] = useState("");
  const [issued, setIssued] = useState<string | null>(null);
  const [copied, setCopied] = useState(false);

  if (issued !== null) {
    return (
      <div className="surface border-mint-400/20 p-5">
        <h2 className="mb-4 text-sm font-medium text-ink-100">Password</h2>
        <p className="mb-3 text-sm text-ink-400">
          Password set and every session revoked. This is the only time it is shown — Praxy keeps only a
          hash. Pass it to <span className="font-mono text-ink-300">{user.email}</span> over something safer
          than email, and have them change it.
        </p>
        <div className="flex items-stretch gap-2">
          <pre className="flex-1 overflow-x-auto rounded-lg border border-ink-700 bg-ink-950 px-3 py-2.5 font-mono text-xs text-ink-100">
            {issued}
          </pre>
          <button
            type="button"
            className="btn-ghost shrink-0 border border-ink-700"
            onClick={() => {
              void navigator.clipboard.writeText(issued);
              setCopied(true);
              setTimeout(() => setCopied(false), 1200);
            }}
          >
            {copied ? "✓" : "Copy"}
          </button>
        </div>
        <button type="button" className="btn-ghost mt-4 border border-ink-700" onClick={() => setIssued(null)}>
          Done
        </button>
      </div>
    );
  }

  return (
    <div className="surface p-5">
      <h2 className="mb-4 text-sm font-medium text-ink-100">Password</h2>
      <Field label="New password" error={fieldError(reset.error, "password")}>
        <div className="flex gap-2">
          <input
            className="input-base font-mono"
            value={draft}
            onChange={(e) => setDraft(e.target.value)}
            placeholder="Type one, or generate"
            autoComplete="off"
            spellCheck={false}
          />
          <button
            type="button"
            className="btn-ghost shrink-0 border border-ink-700"
            onClick={() => setDraft(generatePassword())}
          >
            Generate
          </button>
        </div>
      </Field>
      <div className="mt-3">
        <ConfirmButton
          label="Set password"
          title="Set this user's password?"
          confirmLabel="Set password and revoke sessions"
          successMessage="Password set."
          disabled={draft.length === 0}
          className="btn-ghost border border-ink-700 disabled:opacity-40"
          body={
            <div className="space-y-3">
              <p>
                <span className="font-mono text-ink-300">{user.email}</span> signs in with the new password;
                their old one stops working immediately.
              </p>
              <p>
                <span className="text-coral-400">Every one of their sessions is revoked.</span> An operator
                resets a password because an account is locked out or compromised, and in the second case the
                live sessions are the thing you want gone. Every signed-in client gets a 401 on its next
                request.
              </p>
              <p>The password is shown once after this, and never again.</p>
            </div>
          }
          onConfirm={async () => {
            await reset.mutateAsync(draft);
            setIssued(draft);
            setDraft("");
          }}
        />
      </div>
    </div>
  );
}

/** Field-level message off an ApiError, or undefined — anything else renders as a plain note. */
function fieldError(error: Error | null, field: string): string | undefined {
  return error instanceof ApiError ? error.fieldErrors(field)[0] : undefined;
}

const verifyUrlKey = (projectId: string) => `praxy.verify-url.${projectId}`;

/** 20 chars from a 32-symbol alphabet — ~100 bits, and no character anyone misreads aloud. */
function generatePassword(): string {
  const alphabet = "abcdefghijkmnpqrstuvwxyz23456789";
  const bytes = new Uint8Array(20);
  crypto.getRandomValues(bytes);
  return Array.from(bytes, (byte) => alphabet[byte % alphabet.length]).join("");
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
            <ConfirmButton
              label="Revoke all sessions"
              title="Revoke every session?"
              confirmLabel="Revoke all"
              successMessage="All sessions revoked."
              className="btn-ghost border border-ink-700 text-xs text-coral-400"
              body={
                <>
                  All {sessions.data.total} active session(s) are ended. Every one of this user's signed-in
                  clients gets a 401 on its next request and must sign in again.
                </>
              }
              onConfirm={() => revoke.mutateAsync("all")}
            />
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
                  <ConfirmButton
                    label="Revoke"
                    title="Revoke session?"
                    confirmLabel="Revoke session"
                    successMessage="Session revoked."
                    body={
                      <>
                        This <span className="font-mono text-ink-300">{session.provider}</span> session
                        {session.ip ? <> from <span className="font-mono text-ink-300">{session.ip}</span></> : null} is
                        ended — that client gets a 401 on its next request.
                      </>
                    }
                    onConfirm={() => revoke.mutateAsync(session.id)}
                  />
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
