import { Link, useParams } from "@tanstack/react-router";
import { useState, type FormEvent } from "react";
import { useCreateTopic, useDeleteTopic, useMessagingTopics } from "../api/messaging";
import { ApiError } from "../api/client";
import { DataTable, EmptyState, ErrorNote, Field, FullPageSpinner, IdChip, Modal, Spinner, timeAgo } from "../components/ui";
import { MessagingTabs } from "./MessagingTabs";

const HEADERS = ["Name", "Key", "Subscribers", "Created", ""];

export function MessagingTopicsPage() {
  const { projectId } = useParams({ strict: false }) as { projectId: string };
  const topics = useMessagingTopics(projectId);
  const remove = useDeleteTopic(projectId);
  const [creating, setCreating] = useState(false);

  if (topics.isPending) return <FullPageSpinner />;
  if (topics.isError) throw topics.error;

  return (
    <div>
      <MessagingTabs projectId={projectId} active="topics" />

      <div className="mb-6 flex items-center justify-between">
        <p className="max-w-xl text-xs text-ink-500">A topic groups subscribers; sending to a topic reaches everyone subscribed.</p>
        <button type="button" className="btn-primary shrink-0" onClick={() => setCreating(true)}>
          + Create topic
        </button>
      </div>

      {creating ? <CreateTopicModal projectId={projectId} onClose={() => setCreating(false)} /> : null}

      {topics.data.total === 0 ? (
        <EmptyState
          headers={HEADERS}
          title="No topics yet. Create one, then subscribe users to it."
          action={
            <button type="button" className="btn-primary" onClick={() => setCreating(true)}>
              + Create topic
            </button>
          }
        />
      ) : (
        <DataTable headers={HEADERS}>
          {topics.data.topics.map((topic) => (
            <tr key={topic.id}>
              <td className="px-4 py-3">
                <Link
                  to="/project/$projectId/messaging/topics/$topicId"
                  params={{ projectId, topicId: topic.id }}
                  className="font-medium text-ink-100 hover:text-iris-300"
                >
                  {topic.name}
                </Link>
                <div className="mt-1"><IdChip id={topic.id} /></div>
              </td>
              <td className="px-4 py-3 font-mono text-xs text-ink-400">{topic.key}</td>
              <td className="px-4 py-3 text-xs text-ink-400">{topic.subscriberCount}</td>
              <td className="px-4 py-3 whitespace-nowrap text-ink-400">{timeAgo(topic.createdAt)}</td>
              <td className="px-4 py-3 text-right whitespace-nowrap">
                <Link
                  to="/project/$projectId/messaging/topics/$topicId"
                  params={{ projectId, topicId: topic.id }}
                  className="btn-ghost border border-ink-700 px-2 py-1 text-xs"
                >
                  Subscribers
                </Link>{" "}
                <button
                  type="button"
                  className="btn-ghost border border-ink-700 px-2 py-1 text-xs text-coral-400"
                  disabled={remove.isPending}
                  onClick={() => remove.mutate(topic.id)}
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

function CreateTopicModal({ projectId, onClose }: { projectId: string; onClose: () => void }) {
  const create = useCreateTopic(projectId);
  const [key, setKey] = useState("");
  const [keyTouched, setKeyTouched] = useState(false);
  const [name, setName] = useState("");
  const [description, setDescription] = useState("");
  const error = create.error instanceof ApiError ? create.error : null;

  function slugify(value: string) {
    return value.toLowerCase().replace(/[^a-z0-9-]/g, "-").replace(/-+/g, "-").slice(0, 36) || "topic";
  }

  async function onSubmit(e: FormEvent) {
    e.preventDefault();
    await create.mutateAsync({ key: key || slugify(name), name, description: description || undefined });
    onClose();
  }

  return (
    <Modal title="Create topic" onClose={onClose}>
      <form onSubmit={(e) => void onSubmit(e)} className="space-y-4">
        {error && !error.envelope.fields ? <ErrorNote message={error.message} /> : null}
        <Field label="Name" error={error?.fieldErrors("name")[0]}>
          <input
            className="input-base"
            required
            autoFocus
            value={name}
            onChange={(e) => {
              setName(e.target.value);
              if (!keyTouched) setKey(slugify(e.target.value));
            }}
            placeholder="Product updates"
          />
        </Field>
        <Field label="Key" error={error?.fieldErrors("key")[0]}>
          <input
            className="input-base font-mono"
            required
            value={key}
            onChange={(e) => {
              setKeyTouched(true);
              setKey(e.target.value);
            }}
            placeholder="product-updates"
          />
        </Field>
        <Field label="Description (optional)">
          <input className="input-base" value={description} onChange={(e) => setDescription(e.target.value)} />
        </Field>
        <button type="submit" className="btn-primary w-full" disabled={create.isPending}>
          {create.isPending ? <Spinner /> : "Create topic"}
        </button>
      </form>
    </Modal>
  );
}
