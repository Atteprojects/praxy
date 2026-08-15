import { Navigate, useNavigate } from "@tanstack/react-router";
import { useState, type FormEvent } from "react";
import { ApiError } from "../api/client";
import { useAccount, useCapabilities, useClaim, useLogin } from "../api/queries";
import { ErrorNote, Field, FullPageSpinner, Logo } from "../components/ui";

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

  const { claimed, setupTokenRequired } = capabilities.data;
  return (
    <CenterCard>
      <div className="mb-6 flex flex-col items-center gap-3">
        <Logo size={30} />
        <p className="text-sm text-ink-400">
          {claimed ? "Sign in to your console" : "Claim this instance to get started"}
        </p>
      </div>
      {claimed ? <LoginForm /> : <ClaimForm setupTokenRequired={setupTokenRequired} />}
    </CenterCard>
  );
}

function CenterCard({ children }: { children: React.ReactNode }) {
  return (
    <div className="grid min-h-dvh place-items-center p-4">
      <div className="surface w-full max-w-sm p-8">{children}</div>
    </div>
  );
}

function useFormError() {
  const [error, setError] = useState<ApiError | null>(null);
  const message = error
    ? error.envelope.fields
      ? Object.values(error.envelope.fields).flat().join(" ")
      : error.message
    : null;
  return { message, setError };
}

function LoginForm() {
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
    </form>
  );
}

function ClaimForm({ setupTokenRequired }: { setupTokenRequired: boolean }) {
  const claim = useClaim();
  const navigate = useNavigate();
  const { message, setError } = useFormError();

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
          />
        </Field>
      ) : null}
      <button type="submit" disabled={claim.isPending} className="btn-primary w-full">
        {claim.isPending ? "Claiming…" : "Claim instance"}
      </button>
      <p className="text-center text-xs text-ink-500">
        The first account becomes the instance owner. Sign-up closes afterwards.
      </p>
    </form>
  );
}
