"use client";

import Link from "next/link";
import { useState } from "react";
import { formatInstant, type WebhookSummary, type WebhooksPage } from "@/lib/api";

type Props = {
  endpointId: string;
  initialPage: WebhooksPage;
};

export default function WebhookFeed({ endpointId, initialPage }: Props) {
  const [items, setItems] = useState<WebhookSummary[]>(initialPage.items);
  const [nextBefore, setNextBefore] = useState<string | null>(
    initialPage.nextBefore,
  );
  const [refreshing, setRefreshing] = useState(false);
  const [loadingOlder, setLoadingOlder] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function refresh() {
    setRefreshing(true);
    setError(null);
    try {
      const res = await fetch(`/api/endpoints/${endpointId}/webhooks`, {
        cache: "no-store",
      });
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      const page = (await res.json()) as WebhooksPage;
      setItems(page.items);
      setNextBefore(page.nextBefore);
    } catch {
      setError("Could not refresh the feed.");
    } finally {
      setRefreshing(false);
    }
  }

  async function loadOlder() {
    if (!nextBefore) return;
    setLoadingOlder(true);
    setError(null);
    try {
      const res = await fetch(
        `/api/endpoints/${endpointId}/webhooks?before=${encodeURIComponent(nextBefore)}`,
        { cache: "no-store" },
      );
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      const page = (await res.json()) as WebhooksPage;
      setItems((prev) => [...prev, ...page.items]);
      setNextBefore(page.nextBefore);
    } catch {
      setError("Could not load older requests.");
    } finally {
      setLoadingOlder(false);
    }
  }

  return (
    <section>
      <div
        style={{
          display: "flex",
          justifyContent: "space-between",
          alignItems: "center",
        }}
      >
        <h2>Requests ({items.length})</h2>
        <button
          type="button"
          className="btn"
          onClick={refresh}
          disabled={refreshing}
        >
          {refreshing ? "Refreshing…" : "Refresh"}
        </button>
      </div>
      {error && <div className="error-banner">{error}</div>}
      {items.length === 0 ? (
        <p className="empty-state">
          No requests captured yet. Fire one at the ingest URL above.
        </p>
      ) : (
        items.map((wh) => (
          <Link key={wh.id} href={`/webhooks/${wh.id}`} className="feed-item">
            <div className="feed-row">
              <span>{formatInstant(wh.receivedAt)}</span>
              <span className="method-chip">{wh.method}</span>
              <span className="feed-preview">{wh.bodyPreview || "(no body)"}</span>
            </div>
          </Link>
        ))
      )}
      {nextBefore && (
        <div style={{ textAlign: "center", marginTop: 12 }}>
          <button
            type="button"
            className="btn"
            onClick={loadOlder}
            disabled={loadingOlder}
          >
            {loadingOlder ? "Loading…" : "Load older"}
          </button>
        </div>
      )}
    </section>
  );
}
