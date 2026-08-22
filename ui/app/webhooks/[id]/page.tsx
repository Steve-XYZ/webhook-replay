import Link from "next/link";
import { ApiError, formatInstant, serverGet, type WebhookDetail } from "@/lib/api";
import BodyViewer from "@/components/BodyViewer";
import AttemptsPanel from "@/components/AttemptsPanel";

export const dynamic = "force-dynamic";

export default async function WebhookPage({
  params,
}: PageProps<"/webhooks/[id]">) {
  const { id } = await params;

  let webhook: WebhookDetail;
  try {
    webhook = await serverGet<WebhookDetail>(`/api/webhooks/${id}`);
  } catch (err) {
    if (err instanceof ApiError && err.status === 404) {
      return (
        <div className="not-found">
          <h1>404</h1>
          <p className="dim">This webhook request does not exist.</p>
          <Link href="/">Back to endpoints</Link>
        </div>
      );
    }
    return (
      <div className="error-banner">
        Could not load this webhook. Is the API running on port 5000?
      </div>
    );
  }

  return (
    <>
      <Link href={`/endpoints/${webhook.endpointId}`} className="back-link">
        ← Back to endpoint feed
      </Link>
      <h1>
        <span className="method-chip">{webhook.method}</span>{" "}
        <span className="mono dim">
          {formatInstant(webhook.receivedAt)}
        </span>
      </h1>

      <section className="panel body-viewer">
        <h2>Body</h2>
        <BodyViewer
          bodyText={webhook.bodyText}
          bodyJson={webhook.bodyJson}
        />
      </section>

      <section className="panel">
        <h2>Headers</h2>
        <div className="table-wrap">
          <table className="headers-table">
            <tbody>
              {Object.entries(webhook.headers).map(([name, values]) => (
                <tr key={name}>
                  <th>{name}</th>
                  <td>{values.join(", ")}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </section>

      <AttemptsPanel webhookId={webhook.id} initialAttempts={webhook.attempts} />
    </>
  );
}
