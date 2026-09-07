import { Navigate, useNavigate } from "@tanstack/react-router";
import { useState, type FormEvent } from "react";
import { ApiError } from "../api/client";
import { GoogleIcon, googleSignInUrl, readOAuthError } from "../api/oauth";
import { useAccount, useCapabilities, useClaim, useLogin } from "../api/queries";
import { ErrorNote, Field, Footer, FullPageSpinner, Logo } from "../components/ui";

/**
 * Claim-or-login. A fresh instance shows the claim (register) form; once claimed the
 * register form does not exist — server-enforced and hidden here (Appwrite leaves the
 * button visible and fails at the API; we don't).
 */
export function LoginPage() {
  const capabilities = useCapabilities();
  const account = useAccount();

  // Already signed in — nothing to do here.
  if (account.data) return <Navigate to="/" />;

  if (capabilities.isPending || account.isPending) return <FullPageSpinner />;
  if (capabilities.isError)
    return (
      <CenterCard>
        <ErrorNote message="Cannot reach the Praxy API. Is the server running?" />
      </CenterCard>
    );

  const { claimed, setupTokenRequired, googleOAuthEnabled } = capabilities.data;
  return (
    <CenterCard>
      <div className="mb-6 flex flex-col items-center gap-3">
        <Logo size={30} />
        <p className="text-sm text-ink-400">
          {claimed ? "Sign in to your console" : "Claim this instance to get started"}
        </p>
      </div>
      {claimed ? (
        <LoginForm googleOAuthEnabled={googleOAuthEnabled} />
      ) : (
        <ClaimForm setupTokenRequired={setupTokenRequired} googleOAuthEnabled={googleOAuthEnabled} />
      )}
    </CenterCard>
  );
}

/** A plain navigation link, not a mutation — clicking it leaves the console for Google entirely. */
function GoogleButton({ href }: { href: string }) {
  return (
    <a href={href} className="btn-secondary w-full">
      <GoogleIcon />
      Continue with Google
    </a>
  );
}

function OrDivider() {
  return (
    <div className="flex items-center gap-3 text-xs text-ink-500">
      <div className="h-px flex-1 bg-ink-800" />
      or
      <div className="h-px flex-1 bg-ink-800" />
    </div>
  );
}

function CenterCard({ children }: { children: React.ReactNode }) {
  return (
    <div className="flex min-h-dvh flex-col">
      <div className="grid flex-1 place-items-center p-4">
        <div className="surface w-full max-w-sm p-8">{children}</div>
      </div>
      <Footer />
    </div>
  );
}

function useFormError() {
  const [error, setError] = useState<ApiError | null>(null);
  // Seeds from a failed Google redirect's ?oauthError= once; a subsequent submit's own error
  // (or a second Google attempt, which lands back here with a fresh query string) replaces it.
  const [oauthMessage, setOAuthMessage] = useState(readOAuthError);
  const message = error
    ? error.envelope.fields
      ? Object.values(error.envelope.fields).flat().join(" ")
      : error.message
    : oauthMessage;
  return {
    message,
    setError: (e: ApiError | null) => {
      setOAuthMessage(null);
      setError(e);
    },
  };
}

function LoginForm({ googleOAuthEnabled }: { googleOAuthEnabled: boolean }) {
  const login = useLogin();
  const navigate = useNavigate();
  const { message, setError } = useFormError();

  async function onSubmit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault();
    const data = new FormData(e.currentTarget);
    setError(null);
    try {
      await login.mutateAsync({
        email: data.get("email") as string,
        password: data.get("password") as string,
      });
      await navigate({ to: "/" });
    } catch (err) {
      if (err instanceof ApiError) setError(err);
      else throw err;
    }
  }

  return (
    <form onSubmit={onSubmit} className="space-y-4">
      {message ? <ErrorNote message={message} /> : null}
      <Field label="Email">
        <input name="email" type="email" required autoFocus autoComplete="email" className="input-base" />
      </Field>
      <Field label="Password">
        <input name="password" type="password" required autoComplete="current-password" className="input-base" />
      </Field>
      <button type="submit" disabled={login.isPending} className="btn-primary w-full">
        {login.isPending ? "Signing in…" : "Sign in"}
      </button>
      {googleOAuthEnabled ? (
        <>
          <OrDivider />
          <GoogleButton href={googleSignInUrl({})} />
        </>
      ) : null}
    </form>
  );
}

function ClaimForm({
  setupTokenRequired,
  googleOAuthEnabled,
}: {
  setupTokenRequired: boolean;
  googleOAuthEnabled: boolean;
}) {
  const claim = useClaim();
  const navigate = useNavigate();
  const { message, setError } = useFormError();
  const [setupToken, setSetupToken] = useState("");

  async function onSubmit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault();
    const data = new FormData(e.currentTarget);
    setError(null);
    try {
      await claim.mutateAsync({
        name: data.get("name") as string,
        email: data.get("email") as string,
        password: data.get("password") as string,
        setupToken: setupTokenRequired ? (data.get("setupToken") as string) : undefined,
      });
      await navigate({ to: "/" });
    } catch (err) {
      if (err instanceof ApiError) setError(err);
      else throw err;
    }
  }

  return (
    <form onSubmit={onSubmit} className="space-y-4">
      {message ? <ErrorNote message={message} /> : null}
      <Field label="Name">
        <input name="name" type="text" autoFocus autoComplete="name" className="input-base" />
      </Field>
      <Field label="Email">
        <input name="email" type="email" required autoComplete="email" className="input-base" />
      </Field>
      <Field label="Password">
        <input name="password" type="password" required minLength={8} autoComplete="new-password" className="input-base" />
      </Field>
      {setupTokenRequired ? (
        <Field label="Setup token">
          <input
            name="setupToken"
            type="text"
            required
            placeholder="printed in the server logs"
            className="input-base font-mono"
            value={setupToken}
            onChange={(e) => setSetupToken(e.target.value)}
          />
        </Field>
      ) : null}
      <button type="submit" disabled={claim.isPending} className="btn-primary w-full">
        {claim.isPending ? "Claiming…" : "Claim instance"}
      </button>
      {googleOAuthEnabled ? (
        <>
          <OrDivider />
          <GoogleButton href={googleSignInUrl({ setupToken: setupTokenRequired ? setupToken : undefined })} />
        </>
      ) : null}
      <p className="text-center text-xs text-ink-500">
        The first account becomes the instance owner. Sign-up closes afterwards.
      </p>
    </form>
  );
}
