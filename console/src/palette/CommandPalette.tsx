import { useNavigate, useRouterState } from "@tanstack/react-router";
import { Command } from "cmdk";
import { useEffect, useRef, useState } from "react";
import { useLogout, useProjects } from "../api/queries";
import { Kbd } from "../components/ui";

/**
 * ⌘K palette shell with `g`-prefixed navigation chords (g p → projects, g o → overview).
 * Phase 0 ships the shell; each phase adds its commands.
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
        const params = { projectId: currentProjectId };
        if (e.key === "o") void navigate({ to: "/project/$projectId", params });
        if (e.key === "u") void navigate({ to: "/project/$projectId/auth/users", params });
        if (e.key === "t") void navigate({ to: "/project/$projectId/auth/teams", params });
        if (e.key === "s") void navigate({ to: "/project/$projectId/auth/settings", params });
        if (e.key === "k") void navigate({ to: "/project/$projectId/api-keys", params });
        if (e.key === "d") void navigate({ to: "/project/$projectId/databases", params });
        if (e.key === "r") void navigate({ to: "/project/$projectId/realtime", params });
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
            {currentProjectId ? (
              <>
                <PaletteItem
                  onSelect={() =>
                    run(() => void navigate({ to: "/project/$projectId", params: { projectId: currentProjectId } }))
                  }
                  shortcut="g o"
                >
                  Go to overview
                </PaletteItem>
                <PaletteItem
                  onSelect={() =>
                    run(() =>
                      void navigate({ to: "/project/$projectId/auth/users", params: { projectId: currentProjectId } }),
                    )
                  }
                  shortcut="g u"
                >
                  Go to users
                </PaletteItem>
                <PaletteItem
                  onSelect={() =>
                    run(() =>
                      void navigate({ to: "/project/$projectId/auth/teams", params: { projectId: currentProjectId } }),
                    )
                  }
                  shortcut="g t"
                >
                  Go to teams
                </PaletteItem>
                <PaletteItem
                  onSelect={() =>
                    run(() =>
                      void navigate({
                        to: "/project/$projectId/auth/settings",
                        params: { projectId: currentProjectId },
                      }),
                    )
                  }
                  shortcut="g s"
                >
                  Go to auth settings
                </PaletteItem>
                <PaletteItem
                  onSelect={() =>
                    run(() =>
                      void navigate({ to: "/project/$projectId/api-keys", params: { projectId: currentProjectId } }),
                    )
                  }
                  shortcut="g k"
                >
                  Go to API keys
                </PaletteItem>
                <PaletteItem
                  onSelect={() =>
                    run(() =>
                      void navigate({ to: "/project/$projectId/databases", params: { projectId: currentProjectId } }),
                    )
                  }
                  shortcut="g d"
                >
                  Go to databases
                </PaletteItem>
                <PaletteItem
                  onSelect={() =>
                    run(() =>
                      void navigate({ to: "/project/$projectId/realtime", params: { projectId: currentProjectId } }),
                    )
                  }
                  shortcut="g r"
                >
                  Go to realtime
                </PaletteItem>
                <PaletteItem
                  onSelect={() =>
                    run(() =>
                      void navigate({ to: "/project/$projectId/platforms", params: { projectId: currentProjectId } }),
                    )
                  }
                >
                  Go to platforms
                </PaletteItem>
              </>
            ) : null}
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
