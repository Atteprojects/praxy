import { useEffect, useLayoutEffect, useRef, useState, type ReactNode } from "react";
import { createPortal } from "react-dom";
import { useProjectUsers, useTeam, useTeams, useUser } from "../api/auth";
import { Spinner } from "./ui";

/**
 * The `+ Add role` popover console-design.md calls for: Any / Guests / Users / a searchable user
 * picker / a team picker / label / custom string.
 *
 * Both permission surfaces previously offered a bare text field whose placeholder read
 * `user:<id> · team:<id>/<role> · label:<name>` — meaning the only way to grant a team access was
 * to leave the screen, find the team's 32-hex id, come back and type it correctly. The grammar is
 * still reachable through "Custom", for roles this picker doesn't model.
 */

type Step = "root" | "user" | "team" | "label" | "custom";

const PANEL_WIDTH = 320;
const GAP = 8;

/**
 * The `+ Add role` trigger plus its popover. The panel renders through a portal, positioned
 * against the button: inside the row sheet it would otherwise be clipped by that sheet's
 * `overflow-y-auto`, which cut the top off the list. It flips above the button when there is more
 * room up there, and scrolls internally rather than running off-screen.
 */
export function AddRoleButton({
  projectId,
  existingRoles,
  onPick,
  className = "btn-ghost border border-ink-700 text-xs",
}: {
  projectId: string;
  existingRoles: string[];
  onPick: (role: string) => void;
  className?: string;
}) {
  const [open, setOpen] = useState(false);
  const buttonRef = useRef<HTMLButtonElement>(null);

  return (
    <>
      <button ref={buttonRef} type="button" className={className} onClick={() => setOpen((v) => !v)}>
        + Add role
      </button>
      {open ? (
        <RolePicker
          projectId={projectId}
          existingRoles={existingRoles}
          anchorRef={buttonRef}
          onPick={onPick}
          onClose={() => setOpen(false)}
        />
      ) : null}
    </>
  );
}

/** Exported for RelationshipPicker.tsx, which reuses this same positioning shape. */
export function usePanelPosition(anchorRef: React.RefObject<HTMLElement | null>) {
  const [style, setStyle] = useState<{ top: number; left: number; maxHeight: number } | null>(null);

  useLayoutEffect(() => {
    function place() {
      const rect = anchorRef.current?.getBoundingClientRect();
      if (!rect) return;
      const below = window.innerHeight - rect.bottom - GAP * 2;
      const above = rect.top - GAP * 2;
      const dropDown = below >= above;
      setStyle({
        top: dropDown ? rect.bottom + GAP : Math.max(GAP, rect.top - GAP - Math.min(above, 384)),
        // Right-aligned to the trigger, then clamped so it never runs past either viewport edge.
        left: Math.min(Math.max(GAP, rect.right - PANEL_WIDTH), window.innerWidth - PANEL_WIDTH - GAP),
        maxHeight: Math.min(384, dropDown ? below : above),
      });
    }
    place();
    window.addEventListener("resize", place);
    window.addEventListener("scroll", place, true);
    return () => {
      window.removeEventListener("resize", place);
      window.removeEventListener("scroll", place, true);
    };
  }, [anchorRef]);

  return style;
}

const SIMPLE_ROLES = [
  { role: "any", label: "Anyone", hint: "Public — including unauthenticated requests" },
  { role: "guests", label: "Guests", hint: "Requests with no session" },
  { role: "users", label: "All signed-in users", hint: "Any authenticated app user" },
  { role: "users/verified", label: "Verified users", hint: "Signed in with a verified email" },
] as const;

function RolePicker({
  projectId,
  existingRoles,
  anchorRef,
  onPick,
  onClose,
}: {
  projectId: string;
  /** Already-granted roles, greyed out so the same role isn't added twice. */
  existingRoles: string[];
  anchorRef: React.RefObject<HTMLElement | null>;
  onPick: (role: string) => void;
  onClose: () => void;
}) {
  const [step, setStep] = useState<Step>("root");
  const panelRef = useRef<HTMLDivElement>(null);
  const position = usePanelPosition(anchorRef);

  useEffect(() => {
    function onPointerDown(event: MouseEvent) {
      const target = event.target as Node;
      if (panelRef.current?.contains(target) || anchorRef.current?.contains(target)) return;
      onClose();
    }
    function onKeyDown(event: KeyboardEvent) {
      if (event.key !== "Escape") return;
      // Beat the enclosing dialog's own Escape handler — close the popover, not the sheet.
      event.stopPropagation();
      onClose();
    }
    document.addEventListener("mousedown", onPointerDown);
    document.addEventListener("keydown", onKeyDown, true);
    return () => {
      document.removeEventListener("mousedown", onPointerDown);
      document.removeEventListener("keydown", onKeyDown, true);
    };
  }, [anchorRef, onClose]);

  function pick(role: string) {
    onPick(role);
    onClose();
  }

  return createPortal(
    <div
      ref={panelRef}
      style={{ position: "fixed", top: position?.top ?? -9999, left: position?.left ?? -9999, width: PANEL_WIDTH }}
      className="z-[70] flex flex-col overflow-hidden rounded-xl border border-ink-700 bg-ink-900 shadow-xl shadow-black/50"
    >
      <div className="flex items-center justify-between border-b border-ink-800 px-3 py-2">
        {step === "root" ? (
          <span className="text-xs font-medium text-ink-300">Add role</span>
        ) : (
          <button type="button" className="text-xs text-ink-400 hover:text-ink-100 cursor-pointer" onClick={() => setStep("root")}>
            ← Back
          </button>
        )}
        <button type="button" className="text-ink-500 hover:text-ink-300 cursor-pointer" onClick={onClose} aria-label="Close">
          ✕
        </button>
      </div>

      <div className="min-h-0 flex-1 overflow-y-auto p-2" style={{ maxHeight: position?.maxHeight }}>
        {step === "root" ? (
          <>
            {SIMPLE_ROLES.map((entry) => (
              <PickerRow
                key={entry.role}
                disabled={existingRoles.includes(entry.role)}
                onClick={() => pick(entry.role)}
                hint={entry.hint}
              >
                {entry.label}
              </PickerRow>
            ))}
            <div className="my-1.5 border-t border-ink-800" />
            <PickerRow onClick={() => setStep("user")} hint="Search by email or name">
              A specific user…
            </PickerRow>
            <PickerRow onClick={() => setStep("team")} hint="Everyone on a team, or one team role">
              A team…
            </PickerRow>
            <PickerRow onClick={() => setStep("label")} hint="Users carrying a label you set">
              A label…
            </PickerRow>
            <PickerRow onClick={() => setStep("custom")} hint="Type the role grammar directly">
              Custom…
            </PickerRow>
          </>
        ) : null}

        {step === "user" ? <UserStep projectId={projectId} existingRoles={existingRoles} onPick={pick} /> : null}
        {step === "team" ? <TeamStep projectId={projectId} onPick={pick} /> : null}
        {step === "label" ? <LabelStep onPick={pick} /> : null}
        {step === "custom" ? <CustomStep onPick={pick} /> : null}
      </div>
    </div>,
    document.body,
  );
}

/** Exported for RelationshipPicker.tsx, which reuses this same result-row shape. */
export function PickerRow({
  children,
  hint,
  onClick,
  disabled,
}: {
  children: ReactNode;
  hint?: string;
  onClick: () => void;
  disabled?: boolean;
}) {
  return (
    <button
      type="button"
      disabled={disabled}
      onClick={onClick}
      className="block w-full rounded-lg px-2.5 py-2 text-left transition-colors hover:bg-ink-850 disabled:cursor-not-allowed disabled:opacity-40 disabled:hover:bg-transparent cursor-pointer"
    >
      <span className="block text-sm text-ink-100">{children}</span>
      {hint ? <span className="mt-0.5 block text-xs text-ink-500">{hint}</span> : null}
    </button>
  );
}

function UserStep({
  projectId,
  existingRoles,
  onPick,
}: {
  projectId: string;
  existingRoles: string[];
  onPick: (role: string) => void;
}) {
  const [search, setSearch] = useState("");
  const users = useProjectUsers(projectId, search);

  return (
    <>
      <input
        className="input-base mb-2 text-xs"
        autoFocus
        placeholder="Search email or name…"
        value={search}
        onChange={(e) => setSearch(e.target.value)}
      />
      {users.isPending ? <Spinner className="mx-auto my-3 size-4" /> : null}
      {users.data?.users.length === 0 ? (
        <p className="px-2.5 py-3 text-xs text-ink-500">No users match.</p>
      ) : null}
      {users.data?.users.map(({ user }) => (
        <PickerRow
          key={user.id}
          disabled={existingRoles.includes(`user:${user.id}`)}
          onClick={() => onPick(`user:${user.id}`)}
          hint={user.email}
        >
          {user.name || user.email}
        </PickerRow>
      ))}
    </>
  );
}

function TeamStep({ projectId, onPick }: { projectId: string; onPick: (role: string) => void }) {
  const teams = useTeams(projectId);
  const [teamId, setTeamId] = useState<string | null>(null);
  const [teamRole, setTeamRole] = useState("");

  if (teamId) {
    return (
      <div className="space-y-2 p-1">
        <p className="text-xs text-ink-500">
          Grant to everyone on this team, or narrow it to one membership role.
        </p>
        <input
          className="input-base text-xs"
          autoFocus
          placeholder="role (optional), e.g. owner"
          value={teamRole}
          onChange={(e) => setTeamRole(e.target.value)}
        />
        <button
          type="button"
          className="btn-primary w-full px-2 py-1 text-xs"
          onClick={() => onPick(teamRole.trim() ? `team:${teamId}/${teamRole.trim()}` : `team:${teamId}`)}
        >
          Add role
        </button>
      </div>
    );
  }

  return (
    <>
      {teams.isPending ? <Spinner className="mx-auto my-3 size-4" /> : null}
      {teams.data?.teams.length === 0 ? (
        <p className="px-2.5 py-3 text-xs text-ink-500">No teams yet.</p>
      ) : null}
      {teams.data?.teams.map((team) => (
        <PickerRow key={team.id} onClick={() => setTeamId(team.id)} hint={`${team.memberCount} member(s)`}>
          {team.name}
        </PickerRow>
      ))}
    </>
  );
}

function LabelStep({ onPick }: { onPick: (role: string) => void }) {
  const [label, setLabel] = useState("");
  const valid = /^[a-zA-Z0-9_-]{1,64}$/.test(label);
  return (
    <div className="space-y-2 p-1">
      <p className="text-xs text-ink-500">Matches users carrying this label, set on the user's detail screen.</p>
      <input
        className="input-base text-xs"
        autoFocus
        placeholder="vip"
        value={label}
        onChange={(e) => setLabel(e.target.value)}
        onKeyDown={(e) => e.key === "Enter" && valid && (e.preventDefault(), onPick(`label:${label}`))}
      />
      <button
        type="button"
        className="btn-primary w-full px-2 py-1 text-xs"
        disabled={!valid}
        onClick={() => onPick(`label:${label}`)}
      >
        Add role
      </button>
    </div>
  );
}

function CustomStep({ onPick }: { onPick: (role: string) => void }) {
  const [role, setRole] = useState("");
  return (
    <div className="space-y-2 p-1">
      <p className="text-xs text-ink-500">
        The server validates the shape and rejects anything malformed.
      </p>
      <input
        className="input-base font-mono text-xs"
        autoFocus
        placeholder="member:<id>"
        value={role}
        onChange={(e) => setRole(e.target.value)}
        onKeyDown={(e) => e.key === "Enter" && role.trim() && (e.preventDefault(), onPick(role.trim()))}
      />
      <button
        type="button"
        className="btn-primary w-full px-2 py-1 text-xs"
        disabled={!role.trim()}
        onClick={() => onPick(role.trim())}
      >
        Add role
      </button>
    </div>
  );
}

const STATIC_LABELS: Record<string, string> = {
  any: "Anyone",
  guests: "Guests",
  users: "All signed-in users",
  "users/verified": "Verified users",
};

/**
 * Renders a role string as something a human can read — `team:01a0…/owner` shows the team's real
 * name. The lookups go through the normal query cache, so a role repeated across many rows costs
 * one request, not one per cell.
 */
export function RoleLabel({ projectId, role }: { projectId: string; role: string }) {
  if (STATIC_LABELS[role]) return <Named name={STATIC_LABELS[role]} raw={role} />;
  if (role.startsWith("label:")) return <Named name={`Label: ${role.slice(6)}`} raw={role} />;
  if (role.startsWith("user:")) return <UserRole projectId={projectId} role={role} />;
  if (role.startsWith("team:")) return <TeamRole projectId={projectId} role={role} />;
  return <span className="font-mono text-xs text-ink-200">{role}</span>;
}

function Named({ name, raw }: { name: string; raw: string }) {
  return (
    <span className="block min-w-0">
      <span className="block truncate text-sm text-ink-100">{name}</span>
      {/* The raw grammar stays visible for copying, but truncates rather than widening the
          column — the row sheet is only 448px and the delete checkbox was falling off the edge. */}
      <span className="block truncate font-mono text-[11px] text-ink-500" title={raw}>
        {raw}
      </span>
    </span>
  );
}

function UserRole({ projectId, role }: { projectId: string; role: string }) {
  // `user:<id>` and `user:<id>/verified` both resolve to the same account.
  const userId = role.slice(5).split("/")[0];
  const user = useUser(projectId, userId);
  const suffix = role.endsWith("/verified") ? " (verified)" : "";
  if (!user.data) return <Named name={user.isError ? "Unknown user" : "…"} raw={role} />;
  return <Named name={`${user.data.user.name || user.data.user.email}${suffix}`} raw={role} />;
}

function TeamRole({ projectId, role }: { projectId: string; role: string }) {
  const [teamId, teamRole] = role.slice(5).split("/");
  const team = useTeam(projectId, teamId);
  if (!team.data) return <Named name={team.isError ? "Unknown team" : "…"} raw={role} />;
  return <Named name={teamRole ? `${team.data.name} · ${teamRole}` : team.data.name} raw={role} />;
}
