import Link from "next/link";
import {
  ApiError,
  ingestUrlFor,
  serverGet,
  type Endpoint,
  type WebhooksPage,
} from "@/lib/api";
import WebhookFeed from "@/components/WebhookFeed";
import CopyButton from "@/components/CopyButton";

export const dynamic = "force-dynamic";

export default async function EndpointPage({
  params,
}: PageProps<"/endpoints/[id]">) {
  const { id } = await params;

  let endpoint: Endpoint;
  try {
    endpoint = await serverGet<Endpoint>(`/api/endpoints/${id}`);
  } catch (err) {
    if (err instanceof ApiError && err.status === 404) {
      return (
        <div className="not-found">
          <h1>404</h1>
          <p className="dim">This endpoint does not exist.</p>
          <Link href="/">Back to endpoints</Link>
        </div>
      );
    }
    return (
      <div className="error-banner">
        Could not load this endpoint. Is the API running on port 5000?
      </div>
    );
  }

  let firstPage: WebhooksPage = { items: [], nextBefore: null };
  let feedFailed = false;
  try {
    firstPage = await serverGet<WebhooksPage>(
      `/api/endpoints/${id}/webhooks`,
    );
  } catch {
    feedFailed = true;
  }

  return (
    <>
      <Link href="/" className="back-link">
        ← All endpoints
      </Link>
      <h1>{endpoint.name}</h1>
      <div className="panel">
        <h2>Ingest URL</h2>
        <div className="ingest-box">
          <code>{ingestUrlFor(endpoint.slug)}</code>
          <CopyButton text={ingestUrlFor(endpoint.slug)} />
        </div>
        <p className="dim" style={{ marginBottom: 0 }}>
          Send webhooks here; they are captured and listed below. Forward
          target: <span className="mono">{endpoint.forwardUrl}</span>
        </p>
      </div>
      {feedFailed && (
        <div className="error-banner">
          Could not load captured requests. Is the API running on port 5000?
        </div>
      )}
      <WebhookFeed endpointId={endpoint.id} initialPage={firstPage} />
    </>
  );
}
