import Link from "next/link";

export default function NotFound() {
  return (
    <div className="not-found">
      <h1>404</h1>
      <p className="dim">This page does not exist.</p>
      <Link href="/">Back to endpoints</Link>
    </div>
  );
}
