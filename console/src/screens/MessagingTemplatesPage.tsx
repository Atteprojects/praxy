import { useParams } from "@tanstack/react-router";
import { useEffect, useState } from "react";
import { useMessagingTemplates, useResetTemplate, useSetTemplate } from "../api/messaging";
import { ApiError } from "../api/client";
import type { MessagingTemplate } from "../api/types";
import { Badge, ErrorNote, Field, FullPageSpinner, Spinner } from "../components/ui";
import { MessagingTabs } from "./MessagingTabs";

const LABELS: Record<string, string> = {
  verification: "Email verification",
  recovery: "Password recovery",
  invitation: "Team invitation",
};

const PLACEHOLDERS: Record<string, string[]> = {
  verification: ["{{project}}", "{{url}}", "{{expiryMinutes}}"],
  recovery: ["{{project}}", "{{url}}", "{{expiryMinutes}}"],
  invitation: ["{{project}}", "{{teamName}}", "{{url}}"],
};

export function MessagingTemplatesPage() {
  const { projectId } = useParams({ strict: false }) as { projectId: string };
  const templates = useMessagingTemplates(projectId);

  if (templates.isPending) return <FullPageSpinner />;
  if (templates.isError) throw templates.error;

  return (
    <div>
      <MessagingTabs projectId={projectId} active="templates" />
      <p className="mb-6 max-w-xl text-xs text-ink-500">
        Praxy's own verification, recovery and invitation emails render through these templates.
        Leave a template untouched and it uses Praxy's default text — no project needs to configure
        anything for auth email to keep working.
      </p>
      <div className="max-w-3xl space-y-6">
        {templates.data.templates.map((template) => (
          <TemplateCard key={template.key} projectId={projectId} template={template} />
        ))}
      </div>
    </div>
  );
}

function TemplateCard({ projectId, template }: { projectId: string; template: MessagingTemplate }) {
  const set = useSetTemplate(projectId);
  const reset = useResetTemplate(projectId);
  const [subject, setSubject] = useState(template.subject);
  const [body, setBody] = useState(template.body);
  const error = set.error instanceof ApiError ? set.error : null;

  useEffect(() => {
    setSubject(template.subject);
    setBody(template.body);
  }, [template.subject, template.body]);

  const dirty = subject !== template.subject || body !== template.body;

  return (
    <section className="surface p-5">
      <div className="mb-3 flex items-center gap-2">
        <h2 className="text-sm font-medium text-ink-100">{LABELS[template.key] ?? template.key}</h2>
        {template.overridden ? <Badge tone="iris">customized</Badge> : <Badge tone="ink">default</Badge>}
      </div>
      {error ? <div className="mb-3"><ErrorNote message={error.message} /></div> : null}
      <div className="space-y-3">
        <Field label="Subject">
          <input className="input-base" value={subject} onChange={(e) => setSubject(e.target.value)} />
        </Field>
        <Field label="Body">
          <textarea
            className="input-base h-32 font-mono text-xs"
            value={body}
            onChange={(e) => setBody(e.target.value)}
          />
        </Field>
        <p className="text-xs text-ink-500">
          Placeholders: {PLACEHOLDERS[template.key]?.map((p) => (
            <code key={p} className="mr-1 rounded bg-ink-850 px-1 py-0.5 text-ink-300">{p}</code>
          ))}
        </p>
        <div className="flex gap-2">
          <button
            type="button"
            className="btn-primary"
            disabled={!dirty || set.isPending}
            onClick={() => set.mutate({ key: template.key, subject, body })}
          >
            {set.isPending ? <Spinner /> : "Save"}
          </button>
          <button
            type="button"
            className="btn-ghost border border-ink-700 text-xs"
            disabled={!template.overridden || reset.isPending}
            onClick={() => reset.mutate(template.key)}
          >
            {reset.isPending ? <Spinner /> : "Reset to default"}
          </button>
        </div>
      </div>
    </section>
  );
}
