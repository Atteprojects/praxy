import { Link, useNavigate, useParams } from "@tanstack/react-router";
import { useState, type FormEvent } from "react";
import {
  useAccount,
  useInviteOrganizationMember,
  useOrganization,
  useOrganizationMembers,
  useRemoveOrganizationMember,
  useUpdateOrganizationMemberRole,
} from "../api/queries";
import { ApiError } from "../api/client";
import type { OrganizationMember } from "../api/types";
import { ConfirmButton } from "../components/ConfirmButton";
import { Badge, DataTable, FullPageSpinner, PageHeader, Spinner, timeAgo } from "../components/ui";

/**
 * Invite, accept (a separate public route — see AcceptOrganizationInvitePage), remove, and change
 * role — organizations-phase-2. `owner` manages the roster; `member` sees it read-only except for
 * leaving. The 403 an owner-only mutation returns to a plain member is the actual gate; hiding the
 * controls here is cosmetic, same as OrganizationPage's own danger zone.
 */
export function OrganizationMembersPage() {
  const { organizationId } = useParams({ strict: false }) as { organizationId: string };
  const organization = useOrganization(organizationId);
  const members = useOrganizationMembers(organizationId);
  const account = useAccount();
  const invite = useInviteOrganizationMember(organizationId);
  const [email, setEmail] = useState("");
  const [role, setRole] = useState("member");

  if (organization.isPending || members.isPending || account.isPending) return <FullPageSpinner />;
  if (organization.isError) throw organization.error;
  if (members.isError) throw members.error;

  const isOwner = organization.data.role === "owner";
  const inviteError = invite.error instanceof ApiError ? invite.error : null;

  async function onInvite(e: FormEvent) {
    e.preventDefault();
    await invite.mutateAsync({ email, role, url: `${window.location.origin}/accept-invite` });
    setEmail("");
    setRole("member");
  }

  return (
    <div className="mx-auto w-full max-w-4xl px-6 py-10">
      <Link
        to="/organization/$organizationId"
        params={{ organizationId }}
        className="btn-ghost mb-4 -ml-3 text-xs"
      >
        ← {organization.data.name}
      </Link>

      <PageHeader title="Members" description={`Who can administer or use "${organization.data.name}".`} />

      {isOwner ? (
        <div className="mb-6 max-w-2xl surface p-5">
          <h2 className="mb-3 text-sm font-medium text-ink-100">Invite a member</h2>
          <form onSubmit={(e) => void onInvite(e)} className="flex flex-wrap items-start gap-2">
            <input
              className="input-base min-w-48 flex-1"
              type="email"
              required
              placeholder="colleague@example.com"
              value={email}
              onChange={(e) => setEmail(e.target.value)}
            />
            <select
              className="input-base w-full sm:w-36"
              value={role}
              onChange={(e) => setRole(e.target.value)}
              aria-label="Role"
            >
              <option value="member">member</option>
              <option value="owner">owner</option>
            </select>
            <button type="submit" className="btn-primary" disabled={invite.isPending}>
              {invite.isPending ? <Spinner /> : "Invite"}
            </button>
          </form>
          {inviteError ? <p className="mt-2 text-xs text-coral-400">{inviteError.message}</p> : null}
          <p className="mt-2 text-xs text-ink-500">
            An acceptance email is sent right away. Nothing changes until they accept it.
          </p>
        </div>
      ) : null}

      {members.data.total === 0 ? (
        <p className="py-8 text-center text-sm text-ink-500">No members yet.</p>
      ) : (
        <DataTable headers={["Member", "Role", "Status", "Invited", ""]}>
          {members.data.members.map((member) => (
            <MemberRow
              key={member.userId}
              organizationId={organizationId}
              member={member}
              isOwner={isOwner}
              isSelf={member.userId === account.data?.id}
            />
          ))}
        </DataTable>
      )}
    </div>
  );
}

function MemberRow({
  organizationId,
  member,
  isOwner,
  isSelf,
}: {
  organizationId: string;
  member: OrganizationMember;
  isOwner: boolean;
  isSelf: boolean;
}) {
  const updateRole = useUpdateOrganizationMemberRole(organizationId);
  const remove = useRemoveOrganizationMember(organizationId);
  const roleError = updateRole.error instanceof ApiError ? updateRole.error : null;
  const navigate = useNavigate();

  return (
    <tr>
      <td className="px-4 py-3">
        <span className="block truncate font-medium text-ink-100">
          {member.email}
          {isSelf ? <span className="text-ink-500"> (you)</span> : null}
        </span>
        <span className="block truncate text-xs text-ink-500">{member.name || "—"}</span>
      </td>
      <td className="px-4 py-3">
        {isOwner ? (
          <div className="flex flex-col gap-1">
            <select
              className="input-base w-32 text-xs"
              value={member.role}
              disabled={updateRole.isPending}
              onChange={(e) => void updateRole.mutateAsync({ userId: member.userId, role: e.target.value })}
            >
              <option value="owner">owner</option>
              <option value="member">member</option>
            </select>
            {roleError ? <span className="text-xs text-coral-400">{roleError.message}</span> : null}
          </div>
        ) : (
          <Badge tone={member.role === "owner" ? "iris" : "ink"}>{member.role}</Badge>
        )}
      </td>
      <td className="px-4 py-3">
        {/* "active", not "member": this column is confirmed-state, and "member" is also one of the
            two role names, so an owner's row read "owner ... member" — as if their role were wrong. */}
        {member.confirmed ? <Badge tone="mint">active</Badge> : <Badge tone="amber">invited</Badge>}
      </td>
      <td className="px-4 py-3 whitespace-nowrap text-ink-400">{timeAgo(member.invitedAt ?? member.createdAt)}</td>
      <td className="px-4 py-3 text-right">
        {isSelf || isOwner ? (
          <ConfirmButton
            label={isSelf ? "Leave" : "Remove"}
            title={isSelf ? "Leave organization?" : "Remove member?"}
            confirmLabel={isSelf ? "Leave organization" : "Remove member"}
            successMessage={isSelf ? "You left the organization." : `Removed ${member.email}.`}
            body={
              isSelf ? (
                <>You will lose access to every project in this organization.</>
              ) : (
                <>
                  <span className="font-mono text-ink-300">{member.email}</span> loses access to every project
                  in this organization.
                </>
              )
            }
            onConfirm={async () => {
              await remove.mutateAsync(member.userId);
              // Leaving the organization you're currently looking at makes this very page's
              // queries 404 an instant later — hop back to "/" first, same as the danger zone's
              // own delete does, so HomeRedirect resolves wherever the operator belongs now.
              if (isSelf) await navigate({ to: "/" });
            }}
          />
        ) : null}
      </td>
    </tr>
  );
}
