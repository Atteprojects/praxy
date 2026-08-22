import { useParams } from "@tanstack/react-router";
import { useEffect, useState } from "react";
import { useAuthSettings, useUpdateAuthSettings } from "../api/auth";
import { ApiError } from "../api/client";
import {
  ErrorNote, Field, FullPageSpinner, Spinner, Toggle,
} from "../components/ui";
import { AuthTabs } from "./AuthTabs";

/**
 * Method toggles, Google credentials, session limit, password policy. Minimal options by
 * design — email+password and Google are the only methods until the owner says otherwise.
 */
export function AuthSettingsPage() {
  const { projectId } = useParams({ strict: false }) as { projectId: string };
  const settings = useAuthSettings(projectId);
  const update = useUpdateAuthSettings(projectId);

  const [emailPassword, setEmailPassword] = useState(true);
  const [googleEnabled, setGoogleEnabled] = useState(false);
  const [googleClientId, setGoogleClientId] = useState("");
  const [googleClientSecret, setGoogleClientSecret] = useState("");
  const [sessionLimit, setSessionLimit] = useState(10);
  const [passwordMinLength, setPasswordMinLength] = useState(8);
  const [saved, setSaved] = useState(false);

  useEffect(() => {
    if (settings.data) {
      setEmailPassword(settings.data.emailPassword);
      setGoogleEnabled(settings.data.googleEnabled);
      setGoogleClientId(settings.data.googleClientId ?? "");
      setSessionLimit(settings.data.sessionLimit);
      setPasswordMinLength(settings.data.passwordMinLength);
    }
  }, [settings.data]);

  if (settings.isPending) return <FullPageSpinner />;
  if (settings.isError) throw settings.error;

  const error = update.error instanceof ApiError ? update.error : null;
  const callbackUrl = `${window.location.origin}/v1/account/sessions/oauth2/callback/google/${projectId}`;

  async function onSave() {
    await update.mutateAsync({
      emailPassword,
      googleEnabled,
      googleClientId,
      // Empty input = keep the stored secret; the API treats null as keep, "" as clear.
      googleClientSecret: googleClientSecret || undefined,
      sessionLimit,
      passwordMinLength,
    });
    setGoogleClientSecret("");
    setSaved(true);
    setTimeout(() => setSaved(false), 2000);
  }

  return (
    <div>
      <AuthTabs
        projectId={projectId}
        active="settings"
        description="Which sign-in methods this project's apps accept, and the session and password rules applied to them."
      />

      <div className="max-w-2xl space-y-8">
        <section className="surface p-5">
          <h2 className="mb-3 text-sm font-medium text-ink-100">Sign-in methods</h2>
          <div className="space-y-2 divide-y divide-ink-800/60">
            <Toggle
              checked={emailPassword}
              onChange={setEmailPassword}
              label="Email + password"
              description="Signup and login with an email address and password."
            />
            <div className="pt-2">
              <Toggle
                checked={googleEnabled}
                onChange={setGoogleEnabled}
                label="Google OAuth"
                description="Sign in with Google via the token flow with PKCE."
              />
              {googleEnabled ? (
                <div className="mt-4 space-y-4 border-l-2 border-ink-800 pl-4">
                  <Field label="Client ID">
                    <input
                      className="input-base"
                      value={googleClientId}
                      onChange={(e) => setGoogleClientId(e.target.value)}
                      placeholder="1234567890-abc123.apps.googleusercontent.com"
                    />
                  </Field>
                  <Field
                    label={
                      settings.data.googleClientSecretSet
                        ? "Client secret (stored — enter a value to replace)"
                        : "Client secret"
                    }
                  >
                    <input
                      className="input-base"
                      type="password"
                      value={googleClientSecret}
                      onChange={(e) => setGoogleClientSecret(e.target.value)}
                      placeholder={settings.data.googleClientSecretSet ? "••••••••" : "GOCSPX-…"}
                    />
                  </Field>
                  <div>
                    <span className="mb-1.5 block text-xs font-medium uppercase tracking-wide text-ink-400">
                      Authorization callback URL (paste into the Google OAuth client's Authorized redirect URIs)
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
          <h2 className="mb-3 text-sm font-medium text-ink-100">Limits & policy</h2>
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
