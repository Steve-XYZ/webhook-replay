"use client";

import { useState } from "react";
import { formatInstant, type Attempt } from "@/lib/api";
import StatusBadge from "@/components/StatusBadge";

type Props = {
  webhookId: string;
  initialAttempts: Attempt[];
};

export default function AttemptsPanel({ webhookId, initialAttempts }: Props) {
  const [attempts, setAttempts] = useState<Attempt[]>(initialAttempts);
  const [replaying, setReplaying] = useState(false);
  const [result, setResult] = useState<Attempt | null>(null);
  const [error, setError] = useState<string | null>(null);

  async function replay() {
    setReplaying(true);
    setError(null);
    setResult(null);
    try {
      const res = await fetch(`/api/webhooks/${webhookId}/replay`, {
        method: "POST",
      });
      const attempt = (await res.json()) as Attempt;
      if (!res.ok && res.status !== 502) {
        throw new Error(`HTTP ${res.status}`);
      }
      setResult(attempt);
      const listRes = await fetch(`/api/webhooks/${webhookId}/attempts`, {
        cache: "no-store",
      });
      if (listRes.ok) {
        const data = (await listRes.json()) as { items: Attempt[] };
        setAttempts(data.items);
      }
    } catch {
      setError("Replay failed: could not reach the API.");
    } finally {
      setReplaying(false);
    }
  }

  return (
    <section className="panel">
      <div
        style={{
          display: "flex",
          justifyContent: "space-between",
          alignItems: "center",
        }}
      >
        <h2>Delivery attempts ({attempts.length})</h2>
        <button
          type="button"
          className="btn btn-primary"
          onClick={replay}
          disabled={replaying}
        >
          {replaying ? "Replaying…" : "Replay"}
        </button>
      </div>
      {error && <div className="error-banner">{error}</div>}
      {result && (
        <div className="replay-result">
          Last replay:{" "}
          {result.statusCode === null ? (
            <span className="status-badge status-network">
              network failure — the target did not respond
            </span>
          ) : (
            <>
              responded{" "}
              <span
                className={`status-badge ${
                  result.statusCode >= 200 && result.statusCode < 300
                    ? "status-ok"
                    : "status-fail"
                }`}
              >
                {result.statusCode}
              </span>{" "}
              in {result.durationMs} ms
            </>
          )}
        </div>
      )}
      {attempts.length === 0 ? (
        <p className="empty-state">
          No delivery attempts yet. Hit Replay to forward this request.
        </p>
      ) : (
        attempts.map((a) => {
          const cls =
            a.statusCode === null
              ? "attempt-network"
              : a.statusCode >= 200 && a.statusCode < 300
                ? "attempt-ok"
                : "attempt-fail";
          return (
            <div key={a.id} className={`attempt ${cls}`}>
              <div>
                <StatusBadge statusCode={a.statusCode} />
                <span style={{ marginLeft: 10 }}>
                  → <span className="dim">{a.targetUrl}</span>
                </span>
              </div>
              <div className="attempt-details">
                <span>{formatInstant(a.attemptedAt)}</span>
                <span>{a.durationMs} ms</span>
              </div>
              {a.responseBody && (
                <div className="response-body">{a.responseBody}</div>
              )}
            </div>
          );
        })
      )}
    </section>
  );
}
