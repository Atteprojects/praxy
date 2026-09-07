import { useNavigate } from "@tanstack/react-router";
import { useState, type FormEvent } from "react";
import { ApiError } from "../api/client";
import { useAcceptOrganizationInvite } from "../api/queries";
import { ErrorNote, Field, Footer, Logo } from "../components/ui";

/**
 * Public — no session required to accept, same posture as Teams' own membership accept. The
 * organizationId/userId/secret come from the emailed link's query string; a password is only
 * required when the account the invite created has never had one (an existing operator invited
 * into a second organization already does, and leaves it blank).
 */
export function AcceptOrganizationInvitePage() {
  const navigate = useNavigate();
  const accept = useAcceptOrganizationInvite();
  const [password, setPassword] = useState("");
  const [error, setError] = useState<ApiError | null>(null);

  const params = new URLSearchParams(window.location.search);
  const organizationId = params.get("organizationId") ?? "";
  const userId = params.get("userId") ?? "";
  const secret = params.get("secret") ?? "";
  const linkIncomplete = !organizationId || !userId || !secret;

  async function onSubmit(e: FormEvent) {
    e.preventDefault();
    setError(null);
    try {
      await accept.mutateAsync({ organizationId, userId, secret, password: password || undefined });
      await navigate({ to: "/" });
    } catch (err) {
      if (err instanceof ApiError) setError(err);
      else throw err;
    }
  }

  return (
    <div className="flex min-h-dvh flex-col">
      <div className="grid flex-1 place-items-center p-4">
        <div className="surface w-full max-w-sm p-8">
          <div className="mb-6 flex flex-col items-center gap-3">
            <Logo size={30} />
            <p className="text-sm text-ink-400">Accept your invitation</p>
          </div>
          {linkIncomplete ? (
            <ErrorNote message="This invite link is invalid or incomplete." />
          ) : (
            <form onSubmit={(e) => void onSubmit(e)} className="space-y-4">
              {error && !error.envelope.fields ? <ErrorNote message={error.message} /> : null}
              <Field label="Password" error={error?.fieldErrors("password")[0]}>
                <input
                  type="password"
                  className="input-base"
                  autoFocus
                  minLength={8}
                  autoComplete="new-password"
                  placeholder="Set a password to sign in with"
                  value={password}
                  onChange={(e) => setPassword(e.target.value)}
                />
              </Field>
              <button type="submit" disabled={accept.isPending} className="btn-primary w-full">
                {accept.isPending ? "Joining…" : "Accept invitation"}
              </button>
              <p className="text-center text-xs text-ink-500">
                Already have a Praxy account with this email? Leave the password blank.
              </p>
            </form>
          )}
        </div>
      </div>
      <Footer />
    </div>
  );
}
