/**
 * Operator Google sign-in is browser-navigated (a full-page redirect to Google and back), not a
 * `useMutation` like the rest of `queries.ts` — the console's own page literally leaves and
 * returns. The callback lands back on `/` (session cookie already set by the server) on success,
 * or on `/login`/`/accept-invite` with `?oauthError=<type>` on failure — see
 * `ConsoleOAuthService`'s own remarks for why there's no client-side token exchange step here,
 * unlike the app-user OAuth flow.
 */

/** `/login`'s ClaimForm/LoginForm and `/accept-invite` each build this with only the params they have. */
export function googleSignInUrl(params: {
  setupToken?: string;
  organizationId?: string;
  userId?: string;
  secret?: string;
}): string {
  const query = new URLSearchParams();
  if (params.setupToken) query.set("setupToken", params.setupToken);
  if (params.organizationId) query.set("organizationId", params.organizationId);
  if (params.userId) query.set("userId", params.userId);
  if (params.secret) query.set("secret", params.secret);
  const qs = query.toString();
  return `/v1/console/sessions/oauth2/google${qs ? `?${qs}` : ""}`;
}

const MESSAGES: Record<string, string> = {
  console_oauth_not_configured: "Google sign-in is not configured for this instance.",
  console_oauth_account_not_found:
    "No Praxy operator account uses this Google email. Ask an organization owner to invite you first.",
  console_oauth_email_mismatch: "That Google account's email doesn't match the invited address.",
  console_oauth_identity_already_linked:
    "This Google account is already linked to a different Praxy operator account.",
  user_oauth2_provider_error: "Google sign-in failed. Please try again.",
  user_invalid_token: "The sign-in attempt expired or was tampered with. Please try again.",
  instance_setup_token_invalid: "A valid setup token is required to claim this instance.",
  instance_already_claimed: "This instance has already been claimed. Sign in instead.",
  organization_invite_invalid: "This invitation link is invalid or has expired.",
  organization_invite_already_accepted: "This invitation has already been accepted.",
};

/** Reads `oauthError` off the current URL and returns a human message, or null if absent. */
export function readOAuthError(): string | null {
  const type = new URLSearchParams(window.location.search).get("oauthError");
  if (!type) return null;
  return MESSAGES[type] ?? `Google sign-in failed (${type}).`;
}

export function GoogleIcon({ className }: { className?: string }) {
  return (
    <svg viewBox="0 0 20 20" width="16" height="16" className={className} aria-hidden="true">
      <path
        fill="#4285F4"
        d="M19.6 10.23c0-.68-.06-1.32-.17-1.94H10v3.68h5.38a4.6 4.6 0 0 1-2 3.02v2.5h3.23c1.9-1.75 2.99-4.32 2.99-7.26z"
      />
      <path
        fill="#34A853"
        d="M10 20c2.7 0 4.96-.89 6.62-2.42l-3.23-2.5c-.9.6-2.04.96-3.39.96-2.6 0-4.8-1.76-5.59-4.12H1.06v2.59A10 10 0 0 0 10 20z"
      />
      <path
        fill="#FBBC05"
        d="M4.41 11.92A5.99 5.99 0 0 1 4.09 10c0-.67.11-1.32.32-1.92V5.49H1.06A10 10 0 0 0 0 10c0 1.61.39 3.14 1.06 4.51z"
      />
      <path
        fill="#EA4335"
        d="M10 3.96c1.47 0 2.79.5 3.82 1.5l2.86-2.86C14.95.99 12.7 0 10 0 6.09 0 2.7 2.24 1.06 5.49l3.35 2.59C5.2 5.72 7.4 3.96 10 3.96z"
      />
    </svg>
  );
}
