import { Link } from "@tanstack/react-router";

/** Shared top-tab header for Messaging's four sibling areas — same shape as FunctionDetailHeader, but project-scoped rather than resource-scoped since there's no single parent record to hang tabs off. */
export function MessagingTabs({
  projectId,
  active,
}: {
  projectId: string;
  active: "messages" | "topics" | "templates" | "providers";
}) {
  return (
    <div className="mb-6">
      <h1 className="mb-4 text-2xl font-semibold tracking-tight">Messaging</h1>
      <div className="flex gap-1 border-b border-ink-800" role="tablist">
        <TabLink to="/project/$projectId/messaging" label="Messages" active={active === "messages"} projectId={projectId} />
        <TabLink to="/project/$projectId/messaging/topics" label="Topics" active={active === "topics"} projectId={projectId} />
        <TabLink to="/project/$projectId/messaging/templates" label="Templates" active={active === "templates"} projectId={projectId} />
        <TabLink to="/project/$projectId/messaging/providers" label="Providers" active={active === "providers"} projectId={projectId} />
      </div>
    </div>
  );
}

function TabLink({
  to,
  label,
  active,
  projectId,
}: {
  to: string;
  label: string;
  active: boolean;
  projectId: string;
}) {
  const className = `-mb-px border-b-2 px-3 py-2 text-sm font-medium transition-colors ${
    active ? "border-iris-400 text-ink-100" : "border-transparent text-ink-500 hover:text-ink-300"
  }`;
  return (
    <Link to={to} params={{ projectId }} className={className} role="tab" aria-selected={active}>
      {label}
    </Link>
  );
}
