import { useParams } from "@tanstack/react-router";
import { useState, type FormEvent } from "react";
import { useApiKeys, useCreateApiKey, useRevokeApiKey } from "../api/auth";
import { ApiError } from "../api/client";
import { ConfirmButton } from "../components/ConfirmButton";
import { ScopeGrid } from "../components/ScopePicker";
import {
  Badge, DataTable, EmptyState, ErrorNote, Field, FullPageSpinner, IdChip, Modal, PageHeader, Spinner, Toggle, timeAgo,
} from "../components/ui";

const HEADERS = ["Name", "ID", "Scopes", "Last used", "Created", ""];

export function ApiKeysPage() {
  const { projectId } = useParams({ strict: false }) as { projectId: string };
  const keys = useApiKeys(projectId);
  const revoke = useRevokeApiKey(projectId);
  const [creating, setCreating] = useState(false);

  if (keys.isPending) return <FullPageSpinner />;
  if (keys.isError) throw keys.error;

  return (
    <div>
      <PageHeader
        title="API keys"
        description="Server SDKs authenticate with a scoped key sent in X-Praxy-Key."
        actions={
          <button type="button" className="btn-primary" onClick={() => setCreating(true)}>
            + Create key
          </button>
        }
      />

      {creating ? <CreateKeyModal projectId={projectId} onClose={() => setCreating(false)} /> : null}

      {keys.data.total === 0 ? (
        <EmptyState
          headers={HEADERS}
          title="No API keys yet — create one to authenticate a server SDK."
          action={
            <button type="button" className="btn-primary" onClick={() => setCreating(true)}>
              + Create key
            </button>
          }
        />
      ) : (
        <DataTable headers={HEADERS}>
          {keys.data.keys.map((key) => (
            <tr key={key.id}>
              <td className="px-4 py-3 font-medium text-ink-100">{key.name}</td>
              <td className="px-4 py-3">
                <IdChip id={key.id} />
              </td>
              <td className="px-4 py-3">
                <span className="flex flex-wrap gap-1">
                  {key.scopes.map((scope) => (
                    <Badge key={scope} tone="iris">
                      {scope}
                    </Badge>
                  ))}
                  {key.bypassRowPermissions ? <Badge tone="amber">bypasses permissions</Badge> : null}
                </span>
              </td>
              <td className="px-4 py-3 whitespace-nowrap text-ink-400">{timeAgo(key.lastUsedAt)}</td>
              <td className="px-4 py-3 whitespace-nowrap text-ink-400">
                {new Date(key.createdAt).toLocaleDateString()}
              </td>
              <td className="px-4 py-3 text-right">
                <ConfirmButton
                  label="Revoke"
                  title="Revoke API key?"
                  confirmLabel="Revoke key"
                  successMessage={`Revoked "${key.name}".`}
                  body={
                    <>
                      Any client still sending <span className="font-mono text-ink-300">{key.name}</span> starts
                      getting 401s immediately. This cannot be undone — issue a new key to restore access.
                    </>
                  }
                  onConfirm={() => revoke.mutateAsync(key.id)}
                />
              </td>
            </tr>
          ))}
        </DataTable>
      )}
    </div>
  );
}

function CreateKeyModal({ projectId, onClose }: { projectId: string; onClose: () => void }) {
  const create = useCreateApiKey(projectId);
  const [name, setName] = useState("");
  const [scopes, setScopes] = useState<string[]>([]);
  const [bypassRowPermissions, setBypassRowPermissions] = useState(false);
  const [secret, setSecret] = useState<string | null>(null);
  const [copied, setCopied] = useState(false);
  const error = create.error instanceof ApiError ? create.error : null;

  async function onSubmit(e: FormEvent) {
    e.preventDefault();
    const created = await create.mutateAsync({ name, scopes, bypassRowPermissions });
    setSecret(created.secret);
  }

  // Reveal-once: after creation the modal becomes the one and only display of the secret.
  if (secret !== null) {
    return (
      <Modal title="Copy your API key" onClose={onClose}>
        <p className="mb-3 text-sm text-ink-400">
          This is the only time the key is shown. Store it somewhere safe — Praxy keeps only a hash.
        </p>
        <div className="flex items-stretch gap-2">
          <pre className="flex-1 overflow-x-auto rounded-lg border border-ink-700 bg-ink-950 px-3 py-2.5 font-mono text-xs text-ink-100">
            {secret}
          </pre>
          <button
            type="button"
            className="btn-ghost shrink-0 border border-ink-700"
            onClick={() => {
              void navigator.clipboard.writeText(secret);
              setCopied(true);
              setTimeout(() => setCopied(false), 1200);
            }}
          >
            {copied ? "✓" : "Copy"}
          </button>
        </div>
        <button type="button" className="btn-primary mt-4 w-full" onClick={onClose}>
          Done
        </button>
      </Modal>
    );
  }

  return (
    <Modal title="Create API key" onClose={onClose}>
      <form onSubmit={(e) => void onSubmit(e)} className="space-y-4">
        {error && !error.envelope.fields ? <ErrorNote message={error.message} /> : null}
        <Field label="Name" error={error?.fieldErrors("name")[0]}>
          <input
            className="input-base"
            required
            autoFocus
            value={name}
            onChange={(e) => setName(e.target.value)}
            placeholder="Production backend"
          />
        </Field>
        <div>
          <span className="mb-1.5 block text-xs font-medium uppercase tracking-wide text-ink-400">Scopes</span>
          <ScopeGrid value={scopes} onChange={setScopes} />
          {error?.fieldErrors("scopes")[0] ? (
            <span className="mt-1 block text-xs text-coral-400">{error.fieldErrors("scopes")[0]}</span>
          ) : null}
        </div>
        <Toggle
          checked={bypassRowPermissions}
          onChange={setBypassRowPermissions}
          label="Bypass permissions"
          description="Server-only. This key skips the permission layer entirely: row CRUD ignores table- and row-level checks, and it can invoke any function regardless of its execute access. Scopes still apply. Leave off unless this key is a trusted backend."
        />
        <button type="submit" className="btn-primary w-full" disabled={create.isPending || scopes.length === 0}>
          {create.isPending ? <Spinner /> : "Create key"}
        </button>
      </form>
    </Modal>
  );
}
