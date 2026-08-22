import type { Metadata } from "next";
import Link from "next/link";
import "./globals.css";

export const metadata: Metadata = {
  title: "Webhook Replay",
  description: "Self-hosted webhook inspection and replay tool",
};

export default function RootLayout({ children }: LayoutProps<"/">) {
  return (
    <html lang="en">
      <body>
        <header className="topbar">
          <Link href="/" className="brand">
            webhook-<span>replay</span>
          </Link>
          <span className="tagline">inspect · capture · replay</span>
        </header>
        <main className="container">{children}</main>
      </body>
    </html>
  );
}
