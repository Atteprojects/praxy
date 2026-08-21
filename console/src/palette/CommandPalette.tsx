import { useNavigate, useRouterState } from "@tanstack/react-router";
import { Command } from "cmdk";
import { useEffect, useRef, useState } from "react";
import { useLogout, useProjects } from "../api/queries";
import { Kbd } from "../components/ui";

/**
 * Every project-scoped destination the palette and the `g` chords both offer.
 *
 * This list is the single source of truth for the two. They used to be hand-maintained in
 * parallel: the sidebar rendered `g w` / `g f` / `g m` hints next to Webhooks, Functions and
 * Messaging, but the chord handler only ever learned the Phase 0–4 keys, so those three shortcuts
 * silently did nothing and the palette had no entries for them either. Adding a route here now
 * wires the chord, the palette entry and the shortcut hint at once.
 */
const DESTINATIONS = [
  { key: "o", label: "Go to overview", to: "/project/$projectId" },
  { key: "u", label: "Go to users", to: "/project/$projectId/auth/users" },
  { key: "t", label: "Go to teams", to: "/project/$projectId/auth/teams" },
  { key: "s", label: "Go to auth settings", to: "/project/$projectId/auth/settings" },
  { key: "d", label: "Go to databases", to: "/project/$projectId/databases" },
  { key: "r", label: "Go to realtime", to: "/project/$projectId/realtime" },
  { key: "w", label: "Go to webhooks", to: "/project/$projectId/webhooks" },
  { key: "f", label: "Go to functions", to: "/project/$projectId/functions" },
  { key: "i", label: "Go to sites", to: "/project/$projectId/sites" },
  { key: "m", label: "Go to messaging", to: "/project/$projectId/messaging" },
  { key: "k", label: "Go to API keys", to: "/project/$projectId/api-keys" },
  { label: "Go to platforms", to: "/project/$projectId/platforms" },
] as const;

/**
 * ⌘K palette shell with `g`-prefixed navigation chords (g p → projects, g o → overview).
 */
export function CommandPalette() {
  const [open, setOpen] = useState(false);
  const navigate = useNavigate();
  const logout = useLogout();
  const projects = useProjects(open);
  const routerState = useRouterState();
  const pendingChord = useRef<number | null>(null);
  const chordArmed = useRef(false);

  const currentProjectId = routerState.location.pathname.match(/^\/project\/([^/]+)/)?.[1];

  useEffect(() => {
    function isTyping(target: EventTarget | null) {
      return (
        target instanceof HTMLElement &&
        (target.tagName === "INPUT" || target.tagName === "TEXTAREA" || target.isContentEditable)
      );
    }

    function onKeyDown(e: KeyboardEvent) {
      if ((e.metaKey || e.ctrlKey) && e.key.toLowerCase() === "k") {
        e.preventDefault();
        setOpen((v) => !v);
        return;
      }
      if (isTyping(e.target) || open || e.metaKey || e.ctrlKey || e.altKey) return;

      if (chordArmed.current) {
        chordArmed.current = false;
        if (pendingChord.current) window.clearTimeout(pendingChord.current);
        if (e.key === "p") void navigate({ to: "/" });
        if (!currentProjectId) return;
        const destination = DESTINATIONS.find((d) => "key" in d && d.key === e.key);
        if (destination) void navigate({ to: destination.to, params: { projectId: currentProjectId } });
        return;
      }
      if (e.key === "g") {
        chordArmed.current = true;
        pendingChord.current = window.setTimeout(() => {
          chordArmed.current = false;
        }, 800);
      }
    }

    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [open, navigate, currentProjectId]);

  async function run(action: () => void | Promise<void>) {
    setOpen(false);
    await action();
  }

  if (!open) return null;

  return (
    <div
      className="fixed inset-0 z-50 grid place-items-start justify-items-center bg-ink-950/70 pt-[18vh] backdrop-blur-sm"
      onClick={(e) => e.target === e.currentTarget && setOpen(false)}
    >
      <Command
        label="Command palette"
        className="w-full max-w-lg overflow-hidden rounded-xl border border-ink-700 bg-ink-900 shadow-2xl shadow-black/50"
      >
        <Command.Input
          autoFocus
          placeholder="Type a command or search…"
          className="w-full border-b border-ink-800 bg-transparent px-4 py-3.5 text-sm text-ink-100 outline-none placeholder:text-ink-500"
        />
        <Command.List className="max-h-80 overflow-y-auto p-2">
          <Command.Empty className="px-3 py-6 text-center text-sm text-ink-500">
            No results.
          </Command.Empty>

          <Command.Group heading="Navigate" className="[&_[cmdk-group-heading]]:px-3 [&_[cmdk-group-heading]]:py-1.5 [&_[cmdk-group-heading]]:text-xs [&_[cmdk-group-heading]]:text-ink-500">
            <PaletteItem onSelect={() => run(() => void navigate({ to: "/" }))} shortcut="g p">
              Go to projects
            </PaletteItem>
            {currentProjectId
              ? DESTINATIONS.map((destination) => (
                  <PaletteItem
                    key={destination.to}
                    onSelect={() =>
                      run(() => void navigate({ to: destination.to, params: { projectId: currentProjectId } }))
                    }
                    shortcut={"key" in destination ? `g ${destination.key}` : undefined}
                  >
                    {destination.label}
                  </PaletteItem>
                ))
              : null}
            {projects.data?.projects.map((project) => (
              <PaletteItem
                key={project.id}
                onSelect={() =>
                  run(() => void navigate({ to: "/project/$projectId", params: { projectId: project.id } }))
                }
              >
                Open {project.name}
              </PaletteItem>
            ))}
          </Command.Group>

          <Command.Group heading="Account" className="[&_[cmdk-group-heading]]:px-3 [&_[cmdk-group-heading]]:py-1.5 [&_[cmdk-group-heading]]:text-xs [&_[cmdk-group-heading]]:text-ink-500">
            <PaletteItem
              onSelect={() =>
                run(async () => {
                  await logout.mutateAsync();
                  await navigate({ to: "/login" });
                })
              }
            >
              Sign out
            </PaletteItem>
          </Command.Group>
        </Command.List>
      </Command>
    </div>
  );
}

function PaletteItem({
  children,
  onSelect,
  shortcut,
}: {
  children: React.ReactNode;
  onSelect: () => void;
  shortcut?: string;
}) {
  return (
    <Command.Item
      onSelect={onSelect}
      className="flex cursor-pointer items-center justify-between rounded-lg px-3 py-2.5 text-sm text-ink-300 data-[selected=true]:bg-ink-800 data-[selected=true]:text-ink-100"
    >
      <span>{children}</span>
      {shortcut ? <Kbd>{shortcut}</Kbd> : null}
    </Command.Item>
  );
}
