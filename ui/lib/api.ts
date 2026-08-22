import { type components } from "./api-types";

const API_BASE = process.env.API_BASE ?? "http://localhost:5000";

export type CreateEndpointBody = components["schemas"]["Request"];

export type Endpoint = {
  id: string;
  name: string;
  slug: string;
  forwardUrl: string;
  createdAt: string;
};

export type WebhookSummary = {
  id: string;
  method: string;
  receivedAt: string;
  bodyPreview: string;
};

export type WebhooksPage = {
  items: WebhookSummary[];
  nextBefore: string | null;
};

export type Attempt = {
  id: string;
  targetUrl: string;
  statusCode: number | null;
  responseBody: string | null;
  durationMs: number;
  attemptedAt: string;
};

export type WebhookDetail = {
  id: string;
  endpointId: string;
  method: string;
  headers: Record<string, string[]>;
  bodyText: string;
  bodyJson: unknown;
  receivedAt: string;
  attempts: Attempt[];
};

export class ApiError extends Error {
  readonly status: number;

  constructor(status: number) {
    super(`API responded with ${status}`);
    this.status = status;
  }
}

export async function serverGet<T>(path: string): Promise<T> {
  const res = await fetch(`${API_BASE}${path}`, { cache: "no-store" });
  if (!res.ok) throw new ApiError(res.status);
  return (await res.json()) as T;
}

export function ingestUrlFor(slug: string): string {
  return `${process.env.INGEST_BASE ?? "http://localhost:5000"}/hooks/${slug}`;
}

export function formatInstant(iso: string): string {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return iso;
  return `${d.toISOString().slice(0, 10)} ${d.toISOString().slice(11, 19)}Z`;
}
