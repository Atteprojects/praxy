import { useParams } from "@tanstack/react-router";
import { useMemo, useState } from "react";
import { useProjectUsers } from "../api/auth";
import { useMessage, useMessages, useMessagingTopics, useSendMessage } from "../api/messaging";
import { ApiError } from "../api/client";
import type { DataGridColumn } from "../components/DataGrid";
import { DataGrid } from "../components/DataGrid";
import { Badge, EmptyState, ErrorNote, Field, FullPageSpinner, Modal, Sheet, Spinner, timeAgo } from "../components/ui";
import type { MessageTarget, MessageTargetStatus, PraxyMessage } from "../api/types";
import { MessagingTabs } from "./MessagingTabs";

const HEADERS = ["Subject", "Status", "Targets", "Sent"];

export function MessagesPage() {
  const { projectId } = useParams({ strict: false }) as { projectId: string };
  const messages = useMessages(projectId);
  const [composing, setComposing] = useState(false);
  const [selectedId, setSelectedId] = useState<string | null>(null);

  const columns = useMemo<DataGridColumn<PraxyMessage>[]>(() => [
    {
      id: "subject",
      header: "Subject",
      cell: ({ row }) => <span className="font-medium text-ink-100">{row.original.subject}</span>,
    },
    {
      id: "status",
      header: "Status",
      cell: ({ row }) => (
        row.original.status === "completed" ? <Badge tone="mint">completed</Badge> : <Badge tone="amber">sending</Badge>
      ),
    },
    {
      id: "targets",
      header: "Targets",
      cell: ({ row }) => (
        <span className="text-xs text-ink-400">
          {row.original.topicIds.length > 0 ? `${row.original.topicIds.length} topic(s)` : null}
          {row.original.topicIds.length > 0 && row.original.userIds.length > 0 ? " · " : null}
          {row.original.userIds.length > 0 ? `${row.original.userIds.length} user(s)` : null}
        </span>
      ),
    },
    {
      id: "sent",
      header: "Sent",
      cell: ({ row }) => <span className="text-xs text-ink-400">{timeAgo(row.original.createdAt)}</span>,
    },
  ], []);

  if (messages.isPending) return <FullPageSpinner />;
  if (messages.isError) throw messages.error;

  return (
    <div>
      <MessagingTabs
        projectId={projectId}
        active="messages"
        description="Every message sent from this project, with its delivery status per target."
        actions={
          <button type="button" className="btn-primary" onClick={() => setComposing(true)}>
            + Compose
          </button>
        }
      />

      {composing ? <ComposeModal projectId={projectId} onClose={() => setComposing(false)} onSent={(id) => setSelectedId(id)} /> : null}

      {messages.data.total === 0 ? (
        <EmptyState
          headers={HEADERS}
          title="No messages yet. Compose one to send to a topic or specific users."
          action={
            <button type="button" className="btn-primary" onClick={() => setComposing(true)}>
              + Compose
            </button>
          }
        />
      ) : (
        <DataGrid
          columns={columns}
          data={messages.data.messages}
          getRowId={(row) => row.id}
          onRowClick={(row) => setSelectedId(row.id)}
          maxHeight="65vh"
        />
      )}

      {selectedId ? (
        <MessageSheet projectId={projectId} messageId={selectedId} onClose={() => setSelectedId(null)} />
      ) : null}
    </div>
  );
}

function TargetStatusBadge({ status }: { status: MessageTargetStatus }) {
  if (status === "sent") return <Badge tone="mint">sent</Badge>;
  if (status === "failed") return <Badge tone="coral">failed</Badge>;
  if (status === "sending") return <Badge tone="amber">sending</Badge>;
  return <Badge tone="ink">queued</Badge>;
}

function MessageSheet({
  projectId,
  messageId,
  onClose,
}: {
  projectId: string;
  messageId: string;
  onClose: () => void;
}) {
  const detail = useMessage(projectId, messageId);

  if (detail.isPending) {
    return (
      <Sheet onClose={onClose} title="Message">
        <div className="grid place-items-center py-10"><Spinner /></div>
      </Sheet>
    );
  }
  if (detail.isError) throw detail.error;

  const { message, targets } = detail.data;
  return (
    <Sheet onClose={onClose} title="Message">
      <div className="space-y-4 text-sm">
        <div className="flex items-center gap-2">
          {message.status === "completed" ? <Badge tone="mint">completed</Badge> : <Badge tone="amber">sending</Badge>}
          <span className="text-xs text-ink-400">{message.subject}</span>
        </div>
        <div>
          <span className="mb-1 block text-xs font-medium uppercase tracking-wide text-ink-500">Body</span>
          <pre className="max-h-40 overflow-auto rounded-lg border border-ink-700 bg-ink-950 px-4 py-3 font-mono text-xs whitespace-pre-wrap text-ink-300">
            {message.body}
          </pre>
        </div>
        <div>
          <span className="mb-1 block text-xs font-medium uppercase tracking-wide text-ink-500">
            Delivery status ({targets.length} target{targets.length === 1 ? "" : "s"})
          </span>
          <div className="space-y-1.5">
            {targets.map((target: MessageTarget) => (
              <div key={target.id} className="flex items-center justify-between rounded-lg border border-ink-800 bg-ink-900 px-3 py-2">
                <span className="truncate text-xs text-ink-300">{target.identifier}</span>
                <div className="flex items-center gap-2">
                  {target.error ? <span className="text-xs text-coral-400" title={target.error}>{target.error}</span> : null}
                  <TargetStatusBadge status={target.status} />
                </div>
              </div>
            ))}
          </div>
        </div>
      </div>
    </Sheet>
  );
}

function ComposeModal({
  projectId,
  onClose,
  onSent,
}: {
  projectId: string;
  onClose: () => void;
  onSent: (messageId: string) => void;
}) {
  const topics = useMessagingTopics(projectId);
  const send = useSendMessage(projectId);
  const [subject, setSubject] = useState("");
  const [body, setBody] = useState("");
  const [topicIds, setTopicIds] = useState<string[]>([]);
  const [userSearch, setUserSearch] = useState("");
  const [users, setUsers] = useState<{ id: string; email: string }[]>([]);
  const userResults = useProjectUsers(projectId, userSearch);
  const error = send.error instanceof ApiError ? send.error : null;

  async function onSubmit() {
    const created = await send.mutateAsync({
      subject, body, topicIds, userIds: users.map((u) => u.id),
    });
    onSent(created.id);
    onClose();
  }

  return (
    <Modal title="Compose message" onClose={onClose}>
      <div className="space-y-4">
        {error ? <ErrorNote message={error.message} /> : null}
        <Field label="Subject" error={error?.fieldErrors("subject")[0]}>
          <input className="input-base" required autoFocus value={subject} onChange={(e) => setSubject(e.target.value)} />
        </Field>
        <Field label="Body" error={error?.fieldErrors("body")[0]}>
          <textarea className="input-base h-28 text-sm" required value={body} onChange={(e) => setBody(e.target.value)} />
        </Field>

        {topics.data && topics.data.total > 0 ? (
          <div>
            <span className="mb-1.5 block text-xs font-medium uppercase tracking-wide text-ink-400">Send to topics</span>
            <div className="grid grid-cols-1 gap-2">
              {topics.data.topics.map((topic) => (
                <label
                  key={topic.id}
                  className={`flex cursor-pointer items-center gap-2 rounded-lg border px-3 py-2 text-sm transition-colors ${
                    topicIds.includes(topic.id)
                      ? "border-iris-500/60 bg-iris-500/10 text-ink-100"
                      : "border-ink-700 text-ink-400 hover:border-ink-500"
                  }`}
                >
                  <input
                    type="checkbox"
                    className="hidden"
                    checked={topicIds.includes(topic.id)}
                    onChange={(e) =>
                      setTopicIds((current) =>
                        e.target.checked ? [...current, topic.id] : current.filter((id) => id !== topic.id),
                      )
                    }
                  />
                  <span>{topic.name}</span>
                  <span className="ml-auto text-[11px] text-ink-500">{topic.subscriberCount} subscriber(s)</span>
                </label>
              ))}
            </div>
          </div>
        ) : null}

        <div>
          <span className="mb-1.5 block text-xs font-medium uppercase tracking-wide text-ink-400">Send to specific users</span>
          {users.length > 0 ? (
            <div className="mb-2 flex flex-wrap gap-1">
              {users.map((u) => (
                <span key={u.id} className="inline-flex items-center gap-1 rounded-md border border-ink-700 bg-ink-900 px-2 py-0.5 text-[11px] text-ink-300">
                  {u.email}
                  <button type="button" className="text-ink-500 hover:text-coral-400" onClick={() => setUsers((c) => c.filter((x) => x.id !== u.id))}>✕</button>
                </span>
              ))}
            </div>
          ) : null}
          <input
            className="input-base text-sm"
            value={userSearch}
            onChange={(e) => setUserSearch(e.target.value)}
            placeholder="Search users by email or name…"
          />
          {userSearch && userResults.data && userResults.data.total > 0 ? (
            <div className="mt-1 max-h-32 space-y-1 overflow-y-auto rounded-lg border border-ink-800 p-1">
              {userResults.data.users
                .filter(({ user }) => !users.some((u) => u.id === user.id))
                .map(({ user }) => (
                  <button
                    key={user.id}
                    type="button"
                    className="block w-full rounded px-2 py-1 text-left text-xs text-ink-300 hover:bg-ink-850"
                    onClick={() => {
                      setUsers((c) => [...c, { id: user.id, email: user.email }]);
                      setUserSearch("");
                    }}
                  >
                    {user.email}
                  </button>
                ))}
            </div>
          ) : null}
        </div>

        <button
          type="button"
          className="btn-primary w-full"
          disabled={send.isPending || !subject || !body || (topicIds.length === 0 && users.length === 0)}
          onClick={() => void onSubmit()}
        >
          {send.isPending ? <Spinner /> : "Send"}
        </button>
      </div>
    </Modal>
  );
}
