import { Link } from "@tanstack/react-router";
import { useState } from "react";
import { useProjects } from "../api/queries";
import { FullPageSpinner, IdChip } from "../components/ui";
import { STR } from "../strings";
import { CreateProjectCard } from "./CreateProjectCard";

export function ProjectListPage() {
  const projects = useProjects();
  const [creating, setCreating] = useState(false);

  if (projects.isPending) return <FullPageSpinner />;
  if (projects.isError) throw projects.error;

  // Empty instance: no chrome, just the create card — the Appwrite onboarding
  // pattern, minus the org ceremony.
  if (projects.data.total === 0) return <CreateProjectCard standalone />;

  return (
    <div className="mx-auto w-full max-w-5xl px-6 py-10">
      <div className="mb-8 flex items-center justify-between">
        <h1 className="text-2xl font-semibold tracking-tight">{STR.projects}</h1>
        <button type="button" onClick={() => setCreating(true)} className="btn-primary">
          + Create project
        </button>
      </div>

      {creating ? (
        <div
          className="fixed inset-0 z-40 grid place-items-center bg-ink-950/70 p-4 backdrop-blur-sm"
          onClick={(e) => e.target === e.currentTarget && setCreating(false)}
        >
          <CreateProjectCard />
        </div>
      ) : null}

      <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-3">
        {projects.data.projects.map((project) => (
          <Link
            key={project.id}
            to="/project/$projectId"
            params={{ projectId: project.id }}
            className="surface group block p-5 transition-colors hover:border-iris-500/60"
          >
            <div className="mb-3 flex items-start justify-between gap-2">
              <span className="truncate text-base font-medium group-hover:text-white">{project.name}</span>
              <span
                className={`mt-1.5 size-2 shrink-0 rounded-full ${project.lastPingAt ? "bg-mint-400" : "bg-ink-700"}`}
                title={project.lastPingAt ? "Connected" : "Waiting for first ping"}
              />
            </div>
            <div onClick={(e) => e.preventDefault()}>
              <IdChip id={project.id} />
            </div>
            <p className="mt-3 text-xs text-ink-500">
              Created {new Date(project.createdAt).toLocaleDateString()}
            </p>
          </Link>
        ))}
      </div>
    </div>
  );
}
