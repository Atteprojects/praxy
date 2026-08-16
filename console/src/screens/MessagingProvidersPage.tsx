import { useParams } from "@tanstack/react-router";
import { useState, type FormEvent } from "react";
import {
  useCreateProvider, useDeleteProvider, useMessagingProviders, useUpdateProvider, type ProviderInput,
} from "../api/messaging";
import { ApiError } from "../api/client";
import type { MessagingProvider } from "../api/types";
import {
  Badge, DataTable, EmptyState, ErrorNote, Field, FullPageSpinner, Modal, Spinner, Toggle, timeAgo,
} from "../components/ui";
import { MessagingTabs } from "./MessagingTabs";

const HEADERS = ["Name", "Host", "From", "Status", "Created", ""];

export function MessagingProvidersPage() {
  const { projectId } = useParams({ strict: false }) as { projectId: string };
  const providers = useMessagingProviders(projectId);
  const update = useUpdateProvider(projectId);
  const remove = useDeleteProvider(projectId);
  const [creating, setCreating] = useState(false);
  const [editing, setEditing] = useState<MessagingProvider | null>(null);

  if (providers.isPending) return <FullPageSpinner />;
  if (providers.isError) throw providers.error;

  return (
    <div>
      <MessagingTabs projectId={projectId} active="providers" />

      <div className="mb-6 flex items-center justify-between">
        <p className="max-w-xl text-xs text-ink-500">
          The enabled default provider handles every send — composed messages and Praxy's own
          verification/recovery/invitation emails alike. No provider configured falls back to the
          instance-wide SMTP config (or the dev log, if that's unset either).
        </p>
        <button type="button" className="btn-primary shrink-0" onClick={() => setCreating(true)}>
          + Add provider
        </button>
      </div>

      {creating ? <ProviderModal projectId={projectId} onClose={() => setCreating(false)} /> : null}
      {editing ? <ProviderModal projectId={projectId} provider={editing} onClose={() => setEditing(null)} /> : null}

      {providers.data.total === 0 ? (
        <EmptyState
          headers={HEADERS}
          title="No providers yet. Add an SMTP provider to send email through your own relay."
          action={
            <button type="button" className="btn-primary" onClick={() => setCreating(true)}>
              + Add provider
            </button>
          }
        />
      ) : (
        <DataTable headers={HEADERS}>
          {providers.data.providers.map((provider) => (
            <tr key={provider.id} className="cursor-pointer" onClick={() => setEditing(provider)}>
              <td className="px-4 py-3">
                <span className="font-medium text-ink-100">{provider.name}</span>
              </td>
              <td className="px-4 py-3 font-mono text-xs text-ink-400">{provider.host}:{provider.port}</td>
              <td className="px-4 py-3 font-mono text-xs text-ink-400">{provider.from}</td>
              <td className="px-4 py-3">
                <div className="flex items-center gap-1.5">
                  {provider.enabled ? <Badge tone="mint">enabled</Badge> : <Badge tone="ink">disabled</Badge>}
                  {provider.isDefault ? <Badge tone="iris">default</Badge> : null}
                </div>
              </td>
              <td className="px-4 py-3 whitespace-nowrap text-ink-400">{timeAgo(provider.createdAt)}</td>
              <td className="px-4 py-3 text-right whitespace-nowrap" onClick={(e) => e.stopPropagation()}>
                {!provider.isDefault ? (
                  <button
                    type="button"
                    className="btn-ghost border border-ink-700 px-2 py-1 text-xs"
                    disabled={update.isPending}
                    onClick={() => update.mutate({ providerId: provider.id, isDefault: true })}
                  >
                    Make default
                  </button>
                ) : null}{" "}
                <button
                  type="button"
                  className="btn-ghost border border-ink-700 px-2 py-1 text-xs"
                  disabled={update.isPending}
                  onClick={() => update.mutate({ providerId: provider.id, enabled: !provider.enabled })}
                >
                  {provider.enabled ? "Disable" : "Enable"}
                </button>{" "}
                <button
                  type="button"
                  className="btn-ghost border border-ink-700 px-2 py-1 text-xs text-coral-400"
                  disabled={remove.isPending}
                  onClick={() => remove.mutate(provider.id)}
                >
                  Delete
                </button>
              </td>
            </tr>
          ))}
        </DataTable>
      )}
    </div>
  );
}

function ProviderModal({
  projectId,
  provider,
  onClose,
}: {
  projectId: string;
  provider?: MessagingProvider;
  onClose: () => void;
}) {
  const create = useCreateProvider(projectId);
  const update = useUpdateProvider(projectId);
  const [name, setName] = useState(provider?.name ?? "");
  const [host, setHost] = useState(provider?.host ?? "");
  const [port, setPort] = useState(provider?.port ?? 587);
  const [username, setUsername] = useState(provider?.username ?? "");
  const [from, setFrom] = useState(provider?.from ?? "");
  const [useTls, setUseTls] = useState(provider?.useTls ?? true);
  const [secret, setSecret] = useState("");
  const [isDefault, setIsDefault] = useState(provider?.isDefault ?? false);
  const mutation = provider ? update : create;
  const error = mutation.error instanceof ApiError ? mutation.error : null;

  async function onSubmit(e: FormEvent) {
    e.preventDefault();
    const input: ProviderInput = {
      type: "email", name, host, port, username: username || undefined, from, useTls,
      secret: secret || undefined, isDefault,
    };
    if (provider) {
      await update.mutateAsync({ providerId: provider.id, ...input });
    } else {
      await create.mutateAsync(input);
    }
    onClose();
  }

  return (
    <Modal title={provider ? "Edit provider" : "Add provider"} onClose={onClose}>
      <form onSubmit={(e) => void onSubmit(e)} className="space-y-4">
        {error && !error.envelope.fields ? <ErrorNote message={error.message} /> : null}
        <Field label="Name" error={error?.fieldErrors("name")[0]}>
          <input className="input-base" required autoFocus value={name} onChange={(e) => setName(e.target.value)} placeholder="Primary SMTP" />
        </Field>
        <div className="grid grid-cols-3 gap-3">
          <div className="col-span-2">
            <Field label="Host" error={error?.fieldErrors("host")[0]}>
              <input className="input-base font-mono" required value={host} onChange={(e) => setHost(e.target.value)} placeholder="smtp.example.com" />
            </Field>
          </div>
          <Field label="Port" error={error?.fieldErrors("port")[0]}>
            <input className="input-base" type="number" required value={port} onChange={(e) => setPort(Number(e.target.value))} />
          </Field>
        </div>
        <Field label="Username (optional)">
          <input className="input-base font-mono" value={username} onChange={(e) => setUsername(e.target.value)} />
        </Field>
        <Field label={provider?.hasSecret ? "Password (stored — enter a value to replace)" : "Password (optional)"}>
          <input
            className="input-base"
            type="password"
            value={secret}
            onChange={(e) => setSecret(e.target.value)}
            placeholder={provider?.hasSecret ? "••••••••" : ""}
          />
        </Field>
        <Field label="From address" error={error?.fieldErrors("from")[0]}>
          <input className="input-base" required type="email" value={from} onChange={(e) => setFrom(e.target.value)} placeholder="noreply@example.com" />
        </Field>
        <Toggle checked={useTls} onChange={setUseTls} label="Use TLS" />
        <Toggle checked={isDefault} onChange={setIsDefault} label="Default for email" description="Sends and Praxy's own auth emails use this provider." />
        <button type="submit" className="btn-primary w-full" disabled={mutation.isPending}>
          {mutation.isPending ? <Spinner /> : provider ? "Save" : "Add provider"}
        </button>
      </form>
    </Modal>
  );
}
