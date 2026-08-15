import { useNavigate } from "@tanstack/react-router";
import { useState, type FormEvent } from "react";
import { ApiError } from "../api/client";
import { useCreateProject } from "../api/queries";
import { ErrorNote, Field, Logo } from "../components/ui";

/**
 * The chrome-less centered create-project card — the whole screen for a fresh
 * instance's happy path (orgs stay invisible). Also opened from the project list.
 */
export function CreateProjectCard({ standalone = false }: { standalone?: boolean }) {
  const create = useCreateProject();
  const navigate = useNavigate();
  const [error, setError] = useState<ApiError | null>(null);
  const [showCustomId, setShowCustomId] = useState(false);

  async function onSubmit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault();
    const data = new FormData(e.currentTarget);
    const projectId = (data.get("projectId") as string | null)?.trim();
    setError(null);
    try {
      const project = await create.mutateAsync({
        name: data.get("name") as string,
        projectId: projectId ? projectId : undefined,
      });
      await navigate({ to: "/project/$projectId", params: { projectId: project.id } });
    } catch (err) {
      if (err instanceof ApiError) setError(err);
      else throw err;
    }
  }

  const card = (
    <div className="surface w-full max-w-sm p-8">
      {standalone ? (
        <div className="mb-6 flex flex-col items-center gap-3">
          <Logo size={30} />
          <p className="text-sm text-ink-400">Create your first project</p>
        </div>
      ) : (
        <h2 className="mb-6 text-lg font-semibold">Create project</h2>
      )}
      <form onSubmit={onSubmit} className="space-y-4">
        {error ? <ErrorNote message={error.message} /> : null}
        <Field label="Name" error={error?.fieldErrors("name").join(" ")}>
          <input name="name" type="text" required autoFocus placeholder="My app" className="input-base" />
        </Field>
        {showCustomId ? (
          <Field label="Project ID">
            <input
              name="projectId"
              type="text"
              pattern="[a-z0-9][a-z0-9-]{0,35}"
              placeholder="my-app"
              className="input-base font-mono"
            />
          </Field>
        ) : (
          <button
            type="button"
            onClick={() => setShowCustomId(true)}
            className="text-xs text-iris-300 hover:text-iris-400 transition-colors cursor-pointer"
          >
            Set a custom project ID
          </button>
        )}
        <button type="submit" disabled={create.isPending} className="btn-primary w-full">
          {create.isPending ? "Creating…" : "Create project"}
        </button>
      </form>
    </div>
  );

  return standalone ? <div className="grid min-h-dvh place-items-center p-4">{card}</div> : card;
}
