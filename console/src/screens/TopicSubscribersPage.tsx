import { Link, useParams } from "@tanstack/react-router";
import { useState } from "react";
import { useProjectUsers } from "../api/auth";
import { useMessagingTopic, useSubscribe, useTopicSubscribers, useUnsubscribe } from "../api/messaging";
import { ApiError } from "../api/client";
import { ConfirmButton } from "../components/ConfirmButton";
import { DataTable, EmptyState, ErrorNote, FullPageSpinner, IdChip, Modal, PageHeader, Spinner, timeAgo } from "../components/ui";

const HEADERS = ["Email", "User", "Subscribed", ""];

export function TopicSubscribersPage() {
  const { projectId, topicId } = useParams({ strict: false }) as { projectId: string; topicId: string };
  const topic = useMessagingTopic(projectId, topicId);
  const subscribers = useTopicSubscribers(projectId, topicId);
  const unsubscribe = useUnsubscribe(projectId, topicId);
  const [adding, setAdding] = useState(false);

  if (topic.isPending || subscribers.isPending) return <FullPageSpinner />;
  if (topic.isError) throw topic.error;
  if (subscribers.isError) throw subscribers.error;

  return (
    <div>
      <Link
        to="/project/$projectId/messaging/topics"
        params={{ projectId }}
        className="btn-ghost mb-4 justify-start px-0 text-xs text-ink-500"
      >
        ← Topics
      </Link>
      <PageHeader
        title={topic.data.name}
        chips={<IdChip id={topic.data.id} />}
        description={topic.data.description || undefined}
        actions={
          <button type="button" className="btn-primary" onClick={() => setAdding(true)}>
            + Add subscriber
          </button>
        }
      />

      {adding ? (
        <AddSubscriberModal projectId={projectId} topicId={topicId} onClose={() => setAdding(false)} />
      ) : null}

      {subscribers.data.total === 0 ? (
        <EmptyState headers={HEADERS} title="No subscribers yet. Add an app user to start sending them messages on this topic." />
      ) : (
        <DataTable headers={HEADERS}>
          {subscribers.data.subscribers.map((subscriber) => (
            <tr key={subscriber.id}>
              <td className="px-4 py-3 font-medium text-ink-100">{subscriber.email}</td>
              <td className="px-4 py-3"><IdChip id={subscriber.userId} /></td>
              <td className="px-4 py-3 whitespace-nowrap text-ink-400">{timeAgo(subscriber.createdAt)}</td>
              <td className="px-4 py-3 text-right whitespace-nowrap">
                <ConfirmButton
                  label="Unsubscribe"
                  title="Unsubscribe user?"
                  confirmLabel="Unsubscribe"
                  successMessage={`Unsubscribed ${subscriber.email}.`}
                  body={
                    <>
                      <span className="font-mono text-ink-300">{subscriber.email}</span> stops receiving messages
                      sent to <span className="font-mono text-ink-300">{topic.data.name}</span>.
                    </>
                  }
                  onConfirm={() => unsubscribe.mutateAsync(subscriber.id)}
                />
              </td>
            </tr>
          ))}
        </DataTable>
      )}
    </div>
  );
}

function AddSubscriberModal({
  projectId,
  topicId,
  onClose,
}: {
  projectId: string;
  topicId: string;
  onClose: () => void;
}) {
  const [search, setSearch] = useState("");
  const users = useProjectUsers(projectId, search);
  const subscribe = useSubscribe(projectId, topicId);
  const error = subscribe.error instanceof ApiError ? subscribe.error : null;

  async function onPick(userId: string) {
    await subscribe.mutateAsync(userId);
    onClose();
  }

  return (
    <Modal title="Add subscriber" onClose={onClose}>
      <div className="space-y-3">
        {error ? <ErrorNote message={error.message} /> : null}
        <input
          className="input-base"
          autoFocus
          value={search}
          onChange={(e) => setSearch(e.target.value)}
          placeholder="Search users by email or name…"
        />
        <div className="max-h-72 space-y-1 overflow-y-auto">
          {!users.data ? (
            <div className="grid place-items-center py-6"><Spinner /></div>
          ) : users.data.total === 0 ? (
            <p className="py-4 text-center text-xs text-ink-500">No matching users.</p>
          ) : (
            users.data.users.map(({ user }) => (
              <button
                key={user.id}
                type="button"
                disabled={subscribe.isPending}
                className="flex w-full items-center justify-between rounded-lg border border-ink-800 bg-ink-900 px-3 py-2 text-left text-sm transition-colors hover:border-iris-500/60 disabled:opacity-50"
                onClick={() => void onPick(user.id)}
              >
                <span>
                  <span className="block text-ink-100">{user.email}</span>
                  {user.name ? <span className="block text-xs text-ink-500">{user.name}</span> : null}
                </span>
                {subscribe.isPending ? <Spinner /> : <span className="text-xs text-ink-500">+ Add</span>}
              </button>
            ))
          )}
        </div>
      </div>
    </Modal>
  );
}
