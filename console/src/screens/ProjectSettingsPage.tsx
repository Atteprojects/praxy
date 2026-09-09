import { useNavigate, useParams } from "@tanstack/react-router";
import { useEffect, useState, type FormEvent } from "react";
import { ApiError } from "../api/client";
import { useDeleteProject, useProject, useUpdateProject } from "../api/queries";
import { ErrorNote, Field, FullPageSpinner, IdChip, PageHeader, Spinner } from "../components/ui";

/**
 * The project's own settings: rename, and delete.
 *
 * Both used to live on the Overview screen — the name as an inline-editable title, the danger zone
 * trailing the page. Neither belonged there. An operator scrolling past the project's resources met
 * a delete control they weren't looking for, and an editable title put a mutation on a screen whose
 * whole job is reading numbers. This mirrors what the organization pages already do, so "where do I
 * rename this?" has one answer at both levels.
 *
 * Note that `/auth/settings` is *not* this: that screen configures how an app's own end users sign
 * in (providers, session limits, password policy), which is a feature's settings, not the project's.
 */
export function ProjectSettingsPage() {
  const { projectId } = useParams({ strict: false }) as { projectId: string };
  const project = useProject(projectId);
  const update = useUpdateProject(projectId);

  if (project.isPending) return <FullPageSpinner />;
  if (project.isError) throw project.error;

  return (
    <div>
      <PageHeader
        title={<h1 className="text-2xl font-semibold tracking-tight">Settings</h1>}
        chips={<IdChip id={project.data.id} />}
        description={`Created ${new Date(project.data.createdAt).toLocaleString()}`}
      />

      <div className="space-y-6">
        <RenameProject
          currentName={project.data.name}
          onRename={(name) => update.mutateAsync({ name })}
        />
        <DangerZone projectId={project.data.id} projectName={project.data.name} />
      </div>
    </div>
  );
}

/**
 * `currentName` is the server's value, so the field resyncs when a rename lands rather than holding
 * whatever was last typed. Save stays disabled until the trimmed name actually differs, so the
 * button cannot fire a no-op PATCH. Same shape as the organization's own rename form.
 */
function RenameProject({
  currentName,
  onRename,
}: {
  currentName: string;
  onRename: (name: string) => Promise<unknown>;
}) {
  const [name, setName] = useState(currentName);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<ApiError | null>(null);

  useEffect(() => {
    setName(currentName);
  }, [currentName]);

  async function onSubmit(e: FormEvent) {
    e.preventDefault();
    setError(null);
    setSaving(true);
    try {
      await onRename(name.trim());
    } catch (err) {
      if (err instanceof ApiError) setError(err);
      else throw err;
    } finally {
      setSaving(false);
    }
  }

  const unchanged = name.trim() === currentName || name.trim().length === 0;

  return (
    <form onSubmit={(e) => void onSubmit(e)} className="max-w-3xl surface p-5">
      <h2 className="mb-3 text-sm font-medium text-ink-100">Name</h2>
      {error && !error.envelope.fields ? (
        <div className="mb-3"><ErrorNote message={error.message} /></div>
      ) : null}
      <div className="flex items-end gap-2">
        <div className="min-w-0 flex-1">
          <Field label="Project name" error={error?.fieldErrors("name")[0]}>
            <input
              className="input-base"
              value={name}
              onChange={(e) => setName(e.target.value)}
              placeholder={currentName}
            />
          </Field>
        </div>
        <button type="submit" className="btn-primary shrink-0" disabled={unchanged || saving}>
          {saving ? <Spinner /> : "Save"}
        </button>
      </div>
    </form>
  );
}

/**
 * A hard, confirmed delete — the gap analysis explicitly asked for this, not archiving or a
 * soft-delete. Typed-name confirmation, the same shape `TableSettingsPage.tsx` and
 * `DatabasesPage.tsx`'s database-delete already use: this is strictly more destructive than either
 * (every database, user, key, team and function in the project goes with it), so it gets at least
 * as much friction, not a one-click `ConfirmButton`.
 */
function DangerZone({ projectId, projectName }: { projectId: string; projectName: string }) {
  const navigate = useNavigate();
  const remove = useDeleteProject(projectId);
  const [confirmName, setConfirmName] = useState("");
  const error = remove.error instanceof ApiError ? remove.error : null;

  async function onDelete() {
    await remove.mutateAsync();
    await navigate({ to: "/" });
  }

  return (
    <div className="max-w-3xl surface border-coral-400/20 p-5">
      <h2 className="mb-3 text-sm font-medium text-coral-400">Danger zone</h2>
      <p className="mb-3 text-xs text-ink-500">
        Deleting <span className="font-mono text-ink-300">{projectName}</span> removes every database
        (and every table, column, index and row inside them), every function, user, API key and team
        in this project. This cannot be undone.
      </p>
      {error ? <div className="mb-3"><ErrorNote message={error.message} /></div> : null}
      <p className="mb-2 text-xs text-ink-500">
        Type <span className="font-mono text-ink-300">{projectName}</span> to confirm.
      </p>
      <div className="flex gap-2">
        <input
          className="input-base flex-1"
          value={confirmName}
          onChange={(e) => setConfirmName(e.target.value)}
          placeholder={projectName}
        />
        <button
          type="button"
          className="btn-ghost shrink-0 border border-coral-400/60 text-coral-400 disabled:opacity-40"
          disabled={confirmName !== projectName || remove.isPending}
          onClick={() => void onDelete()}
        >
          {remove.isPending ? <Spinner /> : "Delete project"}
        </button>
      </div>
    </div>
  );
}
