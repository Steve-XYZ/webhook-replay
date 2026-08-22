"use client";

import { useRouter } from "next/navigation";
import { useState } from "react";

export default function EndpointForm() {
  const router = useRouter();
  const [name, setName] = useState("");
  const [slug, setSlug] = useState("");
  const [forwardUrl, setForwardUrl] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);

  async function handleSubmit(e: React.FormEvent<HTMLFormElement>) {
    e.preventDefault();
    setSubmitting(true);
    setError(null);
    try {
      const res = await fetch("/api/endpoints", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ name, slug, forwardUrl }),
      });
      if (res.ok) {
        setName("");
        setSlug("");
        setForwardUrl("");
        router.refresh();
      } else {
        let message = `Request failed with HTTP ${res.status}`;
        try {
          const body = (await res.json()) as { error?: string };
          if (body.error) message = body.error;
        } catch {}
        setError(message);
      }
    } catch {
      setError("Network error: could not reach the API.");
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <form className="panel" onSubmit={handleSubmit}>
      <h2>New endpoint</h2>
      <div className="form-grid">
        <input
          className="input"
          placeholder="Name"
          value={name}
          onChange={(e) => setName(e.target.value)}
          aria-label="Name"
        />
        <input
          className="input"
          placeholder="slug (a-z, 0-9, dashes)"
          value={slug}
          onChange={(e) => setSlug(e.target.value)}
          aria-label="Slug"
        />
        <input
          className="input"
          placeholder="Forward URL (https://...)"
          value={forwardUrl}
          onChange={(e) => setForwardUrl(e.target.value)}
          aria-label="Forward URL"
        />
        <button
          type="submit"
          className="btn btn-primary"
          disabled={submitting || !name || !slug || !forwardUrl}
        >
          {submitting ? "Creating…" : "Create"}
        </button>
        {error && (
          <div role="alert" className="field-error">
            {error}
          </div>
        )}
      </div>
    </form>
  );
}
