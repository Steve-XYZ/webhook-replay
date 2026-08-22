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
  const [overrideTargetUrl, setOverrideTargetUrl] = useState("");
  const [overrideBody, setOverrideBody] = useState("");

  async function runReplay(overrides?: { targetUrl?: string; body?: string }) {
    setReplaying(true);
    setError(null);
    setResult(null);
    try {
      const res = await fetch(`/api/webhooks/${webhookId}/replay`, {
        method: "POST",
        ...(overrides && {
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify(overrides),
        }),
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

  function replay() {
    return runReplay({});
  }

  async function replayWithOverrides(e: React.FormEvent) {
    e.preventDefault();
    await runReplay({
      ...(overrideTargetUrl.trim() && { targetUrl: overrideTargetUrl.trim() }),
      ...(overrideBody !== "" && { body: overrideBody }),
    });
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
      <details style={{ marginTop: 12 }}>
        <summary
          className="dim"
          style={{ cursor: "pointer", userSelect: "none" }}
        >
          Replay with overrides
        </summary>
        <form
          onSubmit={replayWithOverrides}
          style={{
            display: "flex",
            flexDirection: "column",
            gap: 10,
            marginTop: 10,
          }}
        >
          <label style={{ display: "flex", flexDirection: "column", gap: 4 }}>
            <span className="dim">Target URL (optional)</span>
            <input
              type="text"
              className="input"
              placeholder="http://localhost:5803/debug"
              value={overrideTargetUrl}
              onChange={(e) => setOverrideTargetUrl(e.target.value)}
            />
          </label>
          <label style={{ display: "flex", flexDirection: "column", gap: 4 }}>
            <span className="dim">Body (optional, replaces stored body)</span>
            <textarea
              className="input"
              rows={5}
              placeholder='{"orderId":123,"status":"retried"}'
              value={overrideBody}
              onChange={(e) => setOverrideBody(e.target.value)}
            />
          </label>
          <div>
            <button
              type="submit"
              className="btn btn-primary"
              disabled={replaying}
            >
              {replaying ? "Replaying…" : "Replay with overrides"}
            </button>
          </div>
        </form>
      </details>
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
