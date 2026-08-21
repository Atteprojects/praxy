import { createServerClient } from "@praxy/nextjs";
import { redirect } from "next/navigation";
import { appUrl, endpoint, projectId } from "@/lib/config";
import { signIn } from "./actions";

const inputStyle: React.CSSProperties = {
  padding: "8px 10px",
  borderRadius: 6,
  border: "1px solid #2a2a33",
  background: "#15151b",
  color: "#e8e8ee",
  width: "100%",
  boxSizing: "border-box",
};

const buttonStyle: React.CSSProperties = {
  padding: "10px 14px",
  borderRadius: 6,
  border: "none",
  background: "#635bff",
  color: "white",
  fontWeight: 600,
  cursor: "pointer",
};

export default async function SignInPage({
  searchParams,
}: {
  searchParams: Promise<{ error?: string }>;
}) {
  // Server Component read #1: checks whether the httpOnly session cookie already resolves to a
  // real signed-in user. `createServerClient()` is called fresh here — never hoisted to module scope.
  const client = await createServerClient({ endpoint, projectId });
  let signedIn = false;
  try {
    await client.account.get();
    signedIn = true;
  } catch {
    signedIn = false;
  }
  // `redirect()` must not be called inside the try/catch above — it throws internally, and a
  // catch block wrapping it would swallow that throw and silently fall through instead.
  if (signedIn) redirect("/dashboard");

  const { error } = await searchParams;
  const googleUrl =
    `${endpoint}/v1/account/sessions/oauth2/google?project=${encodeURIComponent(projectId)}` +
    `&success=${encodeURIComponent(`${appUrl}/auth/callback`)}` +
    `&failure=${encodeURIComponent(`${appUrl}/?error=oauth`)}`;

  return (
    <main style={{ maxWidth: 360, margin: "80px auto", padding: 24 }}>
      <h1>Praxy Next.js SDK example</h1>
      <p style={{ color: "#9a9aa8" }}>Sign in to reach the dashboard.</p>
      {error && <p style={{ color: "#f87171" }}>Sign-in failed: {error}</p>}
      <form action={signIn} style={{ display: "grid", gap: 12, marginTop: 16 }}>
        <label style={{ display: "grid", gap: 4 }}>
          Email
          <input name="email" type="email" required autoComplete="email" style={inputStyle} />
        </label>
        <label style={{ display: "grid", gap: 4 }}>
          Password
          <input name="password" type="password" required autoComplete="current-password" style={inputStyle} />
        </label>
        <button type="submit" style={buttonStyle}>
          Sign in
        </button>
      </form>
      <p style={{ marginTop: 16 }}>
        <a href={googleUrl} style={{ color: "#a5a5ff" }}>
          Sign in with Google
        </a>
      </p>
      <p style={{ marginTop: 8 }}>
        <a href="/sign-up" style={{ color: "#a5a5ff" }}>
          Need an account? Sign up
        </a>
      </p>
    </main>
  );
}
