import { signUp } from "../actions";

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

export default async function SignUpPage({ searchParams }: { searchParams: Promise<{ error?: string }> }) {
  const { error } = await searchParams;

  return (
    <main style={{ maxWidth: 360, margin: "80px auto", padding: 24 }}>
      <h1>Create an account</h1>
      {error && <p style={{ color: "#f87171" }}>Sign-up failed: {error}</p>}
      <form action={signUp} style={{ display: "grid", gap: 12, marginTop: 16 }}>
        <label style={{ display: "grid", gap: 4 }}>
          Name
          <input name="name" type="text" autoComplete="name" style={inputStyle} />
        </label>
        <label style={{ display: "grid", gap: 4 }}>
          Email
          <input name="email" type="email" required autoComplete="email" style={inputStyle} />
        </label>
        <label style={{ display: "grid", gap: 4 }}>
          Password
          <input name="password" type="password" required autoComplete="new-password" style={inputStyle} />
        </label>
        <button type="submit" style={buttonStyle}>
          Sign up
        </button>
      </form>
      <p style={{ marginTop: 16 }}>
        <a href="/" style={{ color: "#a5a5ff" }}>
          Already have an account? Sign in
        </a>
      </p>
    </main>
  );
}
