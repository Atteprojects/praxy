import type { ReactNode } from "react";

export const metadata = {
  title: "Praxy Next.js SDK example",
  description: "Email sign-in, a Server Component read, a Server Action write, and a realtime Client Component — the SDK's session bridge, end to end.",
};

export default function RootLayout({ children }: { children: ReactNode }) {
  return (
    <html lang="en">
      <body style={{ fontFamily: "system-ui, sans-serif", margin: 0, background: "#0b0b0f", color: "#e8e8ee" }}>
        {children}
      </body>
    </html>
  );
}
