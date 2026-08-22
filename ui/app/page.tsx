import Link from "next/link";
import { ApiError, serverGet, formatInstant, type Endpoint } from "@/lib/api";
import EndpointForm from "@/components/EndpointForm";

export const dynamic = "force-dynamic";

export default async function HomePage() {
  let endpoints: Endpoint[];
  try {
    const data = await serverGet<{ items: Endpoint[] }>("/api/endpoints");
    endpoints = data.items;
  } catch (err) {
    const status = err instanceof ApiError ? err.status : null;
    return (
      <div className="error-banner">
        Could not load endpoints{status ? ` (HTTP ${status})` : ""}. Is the API
        running on port 5000?
      </div>
    );
  }

  return (
    <>
      <h1>Endpoints</h1>
      <EndpointForm />
      {endpoints.length === 0 ? (
        <p className="empty-state">No endpoints yet. Create one above.</p>
      ) : (
        endpoints.map((ep) => (
          <Link
            key={ep.id}
            href={`/endpoints/${ep.id}`}
            className="endpoint-card"
          >
            <div className="endpoint-name">{ep.name}</div>
            <div className="endpoint-meta">
              <span>/{ep.slug}</span>
              <span>{ep.forwardUrl}</span>
              <span>{formatInstant(ep.createdAt)}</span>
            </div>
          </Link>
        ))
      )}
    </>
  );
}
