import { useParams } from "@tanstack/react-router";
import { useState, type FormEvent } from "react";
import { useCreatePlatform, useDeletePlatform, usePlatforms } from "../api/auth";
import { ApiError } from "../api/client";
import { ConfirmButton } from "../components/ConfirmButton";
import {
  Badge, DataTable, EmptyState, ErrorNote, Field, FullPageSpinner, Modal, PageHeader, Spinner,
} from "../components/ui";

const PLATFORM_TYPES = [
  { id: "web", label: "Web", hint: "Hostname joins the CORS + redirect allowlist" },
  { id: "flutter-android", label: "Flutter (Android)", hint: "Package name, e.g. com.example.app" },
  { id: "flutter-ios", label: "Flutter (iOS)", hint: "Bundle ID, e.g. com.example.app" },
] as const;

const HEADERS = ["Platform", "Type", "Hostname", "Added", ""];

/**
 * The platform allowlist. Web hostnames are security-relevant: they gate cross-origin browser
 * calls AND every redirect URL (OAuth success/failure, verification, recovery, invites).
 */
export function PlatformsPage() {
  const { projectId } = useParams({ strict: false }) as { projectId: string };
  const platforms = usePlatforms(projectId);
  const remove = useDeletePlatform(projectId);
  const [adding, setAdding] = useState(false);

  if (platforms.isPending) return <FullPageSpinner />;
  if (platforms.isError) throw platforms.error;

  return (
    <div>
      <PageHeader
        title="Platforms"
        description={
          <>
            Browsers may call this project only from these hostnames, and auth emails/OAuth may only
            redirect to them. A leading <code className="font-mono text-ink-400">*.</code> allows subdomains.
          </>
        }
        actions={
          <button type="button" className="btn-primary" onClick={() => setAdding(true)}>
            + Add platform
          </button>
        }
      />

      {adding ? <AddPlatformModal projectId={projectId} onClose={() => setAdding(false)} /> : null}

      {platforms.data.total === 0 ? (
        <EmptyState
          headers={HEADERS}
          title="No platforms yet — cross-origin calls and redirects are refused until you add one."
          action={
            <button type="button" className="btn-primary" onClick={() => setAdding(true)}>
              + Add platform
            </button>
          }
        />
      ) : (
        <DataTable headers={HEADERS}>
          {platforms.data.platforms.map((platform) => (
            <tr key={platform.id}>
              <td className="px-4 py-3 font-medium text-ink-100">{platform.name}</td>
              <td className="px-4 py-3">
                <Badge tone="iris">{platform.type}</Badge>
              </td>
              <td className="px-4 py-3 font-mono text-xs text-ink-300">{platform.hostname ?? "—"}</td>
              <td className="px-4 py-3 whitespace-nowrap text-ink-400">
                {new Date(platform.createdAt).toLocaleDateString()}
              </td>
              <td className="px-4 py-3 text-right">
                <ConfirmButton
                  label="Remove"
                  title="Remove platform?"
                  confirmLabel="Remove platform"
                  successMessage={`Removed "${platform.name}".`}
                  body={
                    <>
                      Browser calls from{" "}
                      <span className="font-mono text-ink-300">{platform.hostname ?? platform.name}</span> will be
                      refused, and auth emails can no longer redirect there. Live clients on this origin break
                      immediately.
                    </>
                  }
                  onConfirm={() => remove.mutateAsync(platform.id)}
                />
              </td>
            </tr>
          ))}
        </DataTable>
      )}
    </div>
  );
}

function AddPlatformModal({ projectId, onClose }: { projectId: string; onClose: () => void }) {
  const create = useCreatePlatform(projectId);
  const [type, setType] = useState<(typeof PLATFORM_TYPES)[number]["id"]>("web");
  const [name, setName] = useState("");
  const [hostname, setHostname] = useState("");
  const error = create.error instanceof ApiError ? create.error : null;
  const selected = PLATFORM_TYPES.find((t) => t.id === type)!;

  async function onSubmit(e: FormEvent) {
    e.preventDefault();
    await create.mutateAsync({ type, name, hostname: hostname || undefined });
    onClose();
  }

  return (
    <Modal title="Add platform" onClose={onClose}>
      <form onSubmit={(e) => void onSubmit(e)} className="space-y-4">
        {error && !error.envelope.fields ? <ErrorNote message={error.message} /> : null}
        <div className="grid grid-cols-1 gap-2 sm:grid-cols-3">
          {PLATFORM_TYPES.map((platformType) => (
            <button
              key={platformType.id}
              type="button"
              onClick={() => setType(platformType.id)}
              className={`rounded-lg border px-3 py-2.5 text-sm transition-colors ${
                type === platformType.id
                  ? "border-iris-500/60 bg-iris-500/10 text-ink-100"
                  : "border-ink-700 text-ink-400 hover:border-ink-500"
              }`}
            >
              {platformType.label}
            </button>
          ))}
        </div>
        <Field label="Name" error={error?.fieldErrors("name")[0]}>
          <input
            className="input-base"
            required
            autoFocus
            value={name}
            onChange={(e) => setName(e.target.value)}
            placeholder="My web app"
          />
        </Field>
        <Field
          label={type === "web" ? "Hostname" : "Hostname (optional)"}
          error={error?.fieldErrors("hostname")[0]}
        >
          <input
            className="input-base"
            required={type === "web"}
            value={hostname}
            onChange={(e) => setHostname(e.target.value)}
            placeholder={type === "web" ? "app.example.com or *.example.com" : selected.hint}
          />
        </Field>
        <button type="submit" className="btn-primary w-full" disabled={create.isPending}>
          {create.isPending ? <Spinner /> : "Add platform"}
        </button>
      </form>
    </Modal>
  );
}
