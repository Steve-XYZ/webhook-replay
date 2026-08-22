"use client";

import Link from "next/link";
import { useEffect, useRef, useState, type ChangeEvent } from "react";
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
  const [searching, setSearching] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [query, setQuery] = useState("");
  const [activeQuery, setActiveQuery] = useState("");
  const [live, setLive] = useState(false);
  const debounceRef = useRef<ReturnType<typeof setTimeout> | undefined>(
    undefined,
  );

  useEffect(() => () => clearTimeout(debounceRef.current), []);

  function feedUrl(q: string, before?: string | null): string {
    const params = new URLSearchParams();
    if (q !== "") params.set("q", q);
    if (before) params.set("before", before);
    const suffix = params.toString();
    return `/api/endpoints/${endpointId}/webhooks${suffix ? `?${suffix}` : ""}`;
  }

  async function fetchFirstPage(q: string) {
    setSearching(true);
    setError(null);
    try {
      const res = await fetch(feedUrl(q), { cache: "no-store" });
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      const page = (await res.json()) as WebhooksPage;
      setItems(page.items);
      setNextBefore(page.nextBefore);
    } catch {
      setError("Could not search the feed.");
    } finally {
      setSearching(false);
    }
  }

  function handleSearchChange(event: ChangeEvent<HTMLInputElement>) {
    const value = event.target.value;
    setQuery(value);
    clearTimeout(debounceRef.current);
    debounceRef.current = setTimeout(() => {
      const trimmed = value.trim();
      setActiveQuery(trimmed);
      fetchFirstPage(trimmed);
    }, 300);
  }

  useEffect(() => {
    const source = new EventSource(`/api/endpoints/${endpointId}/events`);

    source.addEventListener("open", () => setLive(true));
    source.addEventListener("error", () => setLive(false));
    source.addEventListener("webhook", (event) => {
      try {
        const item = JSON.parse(
          (event as MessageEvent<string>).data,
        ) as WebhookSummary;
        setItems((prev) =>
          prev.some((existing) => existing.id === item.id) || activeQuery !== ""
            ? prev
            : [item, ...prev],
        );
      } catch {
        return;
      }
    });

    return () => source.close();
  }, [endpointId, activeQuery]);

  async function refresh() {
    setRefreshing(true);
    setError(null);
    try {
      const res = await fetch(feedUrl(activeQuery), { cache: "no-store" });
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
      const res = await fetch(feedUrl(activeQuery, nextBefore), {
        cache: "no-store",
      });
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
        <h2>
          Requests ({items.length})
          {activeQuery !== "" && " matching search"}
          <span
            className={`live-dot ${live ? "on" : ""}`}
            title={live ? "Live updates connected" : "Live feed connecting…"}
            aria-label={live ? "Live" : "Connecting"}
          />
        </h2>
        <button
          type="button"
          className="btn"
          onClick={refresh}
          disabled={refreshing || searching}
        >
          {refreshing ? "Refreshing…" : "Refresh"}
        </button>
      </div>
      <input
        type="search"
        className="input"
        placeholder="Search body text…"
        aria-label="Search request bodies"
        value={query}
        onChange={handleSearchChange}
        style={{ margin: "8px 0" }}
      />
      {error && <div className="error-banner">{error}</div>}
      {items.length === 0 ? (
        <p className="empty-state">
          {activeQuery !== "" ? (
            <>No requests match &ldquo;{query}&rdquo;.</>
          ) : (
            <>No requests captured yet. Fire one at the ingest URL above.</>
          )}
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
            disabled={loadingOlder || searching}
          >
            {loadingOlder ? "Loading…" : "Load older"}
          </button>
        </div>
      )}
    </section>
  );
}
