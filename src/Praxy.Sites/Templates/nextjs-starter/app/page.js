// Forces this page to render on every request instead of being statically prerendered at build
// time — so the timestamp below actually proves live server-side rendering, not a cached shell.
export const dynamic = "force-dynamic";

export default function Page() {
  const renderedAt = new Date().toISOString();

  return (
    <main
      style={{
        fontFamily:
          "-apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif",
        maxWidth: 640,
        margin: "0 auto",
        padding: "4rem 1.5rem",
        color: "#1a1a1a",
      }}
    >
      <h1 style={{ fontSize: "1.75rem", marginBottom: "0.5rem" }}>
        🎉 Your Praxy site is live
      </h1>
      <p style={{ color: "#555", lineHeight: 1.6 }}>
        This is the Praxy Sites starter template — a minimal Next.js app with{" "}
        <code>output: &quot;standalone&quot;</code> already set, so it builds
        and deploys with no configuration.
      </p>
      <p style={{ color: "#555", lineHeight: 1.6 }}>
        This page is server-rendered on every request, not a static shell —
        proof:
      </p>
      <p
        style={{
          fontFamily: "ui-monospace, monospace",
          fontSize: "0.875rem",
          background: "#f4f4f5",
          padding: "0.75rem 1rem",
          borderRadius: "0.5rem",
        }}
      >
        Rendered at {renderedAt}
      </p>
      <p style={{ color: "#555", lineHeight: 1.6 }}>
        Edit <code>app/page.js</code>, package your app as a{" "}
        <code>.tar</code>, and upload it from this site&apos;s Deployments
        tab to replace this page with your own.
      </p>
    </main>
  );
}
