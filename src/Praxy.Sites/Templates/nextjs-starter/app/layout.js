export const metadata = {
  title: "Praxy site",
  description: "A Next.js app hosted on Praxy Sites.",
};

export default function RootLayout({ children }) {
  return (
    <html lang="en">
      <body style={{ margin: 0 }}>{children}</body>
    </html>
  );
}
