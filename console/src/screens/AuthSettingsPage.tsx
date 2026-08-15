import { useParams } from "@tanstack/react-router";
import { useEffect, useState } from "react";
import { useAuthSettings, useUpdateAuthSettings } from "../api/auth";
import { ApiError } from "../api/client";
import { ErrorNote, Field, FullPageSpinner, Spinner, Toggle } from "../components/ui";

/**
 * Method toggles, GitHub credentials, session limit, password policy. Minimal options by
 * design — email+password and GitHub are the only methods until the owner says otherwise.
 */
export function AuthSettingsPage() {
  const { projectId } = useParams({ strict: false }) as { projectId: string };
  const settings = useAuthSettings(projectId);
  const update = useUpdateAuthSettings(projectId);

  const [emailPassword, setEmailPassword] = useState(true);
  const [githubEnabled, setGithubEnabled] = useState(false);
  const [githubClientId, setGithubClientId] = useState("");
  const [githubClientSecret, setGithubClientSecret] = useState("");
  const [sessionLimit, setSessionLimit] = useState(10);
  const [passwordMinLength, setPasswordMinLength] = useState(8);
  const [saved, setSaved] = useState(false);

  useEffect(() => {
    if (settings.data) {
      setEmailPassword(settings.data.emailPassword);
      setGithubEnabled(settings.data.githubEnabled);
      setGithubClientId(settings.data.githubClientId ?? "");
      setSessionLimit(settings.data.sessionLimit);
      setPasswordMinLength(settings.data.passwordMinLength);
    }
  }, [settings.data]);

  if (settings.isPending) return <FullPageSpinner />;
  if (settings.isError) throw settings.error;

  const error = update.error instanceof ApiError ? update.error : null;
  const callbackUrl = `${window.location.origin}/v1/account/sessions/oauth2/callback/github/${projectId}`;

  async function onSave() {
    await update.mutateAsync({
      emailPassword,
      githubEnabled,
      githubClientId,
      // Empty input = keep the stored secret; the API treats null as keep, "" as clear.
      githubClientSecret: githubClientSecret || undefined,
      sessionLimit,
      passwordMinLength,
    });
    setGithubClientSecret("");
    setSaved(true);
    setTimeout(() => setSaved(false), 2000);
  }

  return (
    <div className="max-w-2xl">
      <h1 className="mb-6 text-2xl font-semibold tracking-tight">Auth settings</h1>

      <div className="space-y-6">
        <section className="surface p-5">
          <h2 className="mb-3 text-sm font-medium uppercase tracking-wide text-ink-500">Sign-in methods</h2>
          <div className="space-y-2 divide-y divide-ink-800/60">
            <Toggle
              checked={emailPassword}
              onChange={setEmailPassword}
              label="Email + password"
              description="Signup and login with an email address and password."
            />
            <div className="pt-2">
              <Toggle
                checked={githubEnabled}
                onChange={setGithubEnabled}
                label="GitHub OAuth"
                description="Sign in with GitHub via the token flow with PKCE."
              />
              {githubEnabled ? (
                <div className="mt-4 space-y-4 border-l-2 border-ink-800 pl-4">
                  <Field label="Client ID">
                    <input
                      className="input-base"
                      value={githubClientId}
                      onChange={(e) => setGithubClientId(e.target.value)}
                      placeholder="Iv1.abc123…"
                    />
                  </Field>
                  <Field
                    label={
                      settings.data.githubClientSecretSet
                        ? "Client secret (stored — enter a value to replace)"
                        : "Client secret"
                    }
                  >
                    <input
                      className="input-base"
                      type="password"
                      value={githubClientSecret}
                      onChange={(e) => setGithubClientSecret(e.target.value)}
                      placeholder={settings.data.githubClientSecretSet ? "••••••••" : "ghp_…"}
                    />
                  </Field>
                  <div>
                    <span className="mb-1.5 block text-xs font-medium uppercase tracking-wide text-ink-400">
                      Authorization callback URL (paste into the GitHub OAuth app)
                    </span>
                    <pre className="overflow-x-auto rounded-lg border border-ink-700 bg-ink-950 px-3 py-2 font-mono text-xs text-ink-300">
                      {callbackUrl}
                    </pre>
                  </div>
                </div>
              ) : null}
            </div>
          </div>
        </section>

        <section className="surface p-5">
          <h2 className="mb-3 text-sm font-medium uppercase tracking-wide text-ink-500">Limits & policy</h2>
          <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
            <Field label="Session limit per user">
              <input
                className="input-base"
                type="number"
                min={1}
                max={100}
                value={sessionLimit}
                onChange={(e) => setSessionLimit(Number(e.target.value))}
              />
            </Field>
            <Field label="Minimum password length">
              <input
                className="input-base"
                type="number"
                min={8}
                max={128}
                value={passwordMinLength}
                onChange={(e) => setPasswordMinLength(Number(e.target.value))}
              />
            </Field>
          </div>
          <p className="mt-3 text-xs text-ink-500">
            Creating a session past the limit evicts the user's oldest session.
          </p>
        </section>

        {error ? <ErrorNote message={error.message} /> : null}

        <button type="button" className="btn-primary" disabled={update.isPending} onClick={() => void onSave()}>
          {update.isPending ? <Spinner /> : saved ? "Saved ✓" : "Save settings"}
        </button>
      </div>
    </div>
  );
}
