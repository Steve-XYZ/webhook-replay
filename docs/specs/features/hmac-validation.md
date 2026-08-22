# Feature: HMAC-SHA256 signature validation on ingest

Status: shaped, ready to deliver · Owner: this cycle · Slice: post-MVP roadmap item #3 (`05-roadmap-post-mvp.md`)

## Problem

`POST /hooks/{slug}` accepts and persists every request that reaches it. Anyone who discovers a slug can pollute a feed or inject payloads that will later be replayed to the real target. Providers sign their webhooks; the ingest route ignores signatures entirely. For an integration-debugging tool whose whole value is "what we store is what the provider sent", unauthenticated ingest undermines trust in stored requests.

## Goals

- Optional per-endpoint shared-secret HMAC-SHA256 validation at ingest.
- Fail closed when a secret exists and the signature is missing/invalid: `401`, request NOT persisted.
- Zero behavior change for endpoints without a secret.
- Secret handled so it cannot leak through read paths.

## Non-goals

- Per-provider schemes (Stripe `t=,v1=` timestamp envelopes, GitHub `sha1` fallback). One scheme only — see D1.
- Secret rotation, regeneration, or per-endpoint key management UI. Rotation today = create a new endpoint.
- Signature validation on replay outbound calls. Replay sends the request as stored.
- Rate-limiting changes, body size limit changes.

## Decisions

| # | Decision | Rationale |
|---|---|---|
| D1 | Single scheme: header `X-Webhook-Signature`, value optional `sha256=` prefix + lowercase hex of HMAC-SHA256 over the EXACT raw request bytes. | This is a debugging tool, not a provider adapter framework. Supporting N provider envelope formats multiplies parsing surface and failure modes without serving the core loop. One deterministic scheme (raw-bytes HMAC) is what generic signing libraries produce; providers with exotic envelopes can be fronted by a transform proxy until pluggable schemes are justified post-MVP. |
| D2 | Fail closed AND fail clean: secret set + missing/malformed/invalid signature → `401` with `{ "error": ... }`, nothing written to `webhook_requests`. Missing vs invalid returns the same message. | Storing rejected payloads would pollute feeds and leak untrusted data into replay history. Not persisting keeps "everything in the feed passed verification". A single error message avoids telling attackers whether their header format was wrong vs the digest. |
| D3 | Secret accepted optionally at endpoint creation; echoed ONLY in the `201` response; never returned by GET list/detail. UI shows it once after creation in a password-style field. | Matches established webhook-signing-secret UX (Stripe/GitHub): show once, copy now, gone later. Read-back endpoints are the common leak path (browser history, shared screens, log aggregation of API responses). The secret remains editable via SQL for operators who need it. |
| D4 | Comparison uses `System.Security.Cryptography.CryptographicOperations.FixedTimeEquals` on the normalized provided hex vs computed hex. Length checked up front. | Prevents byte-wise timing oracles on the digest comparison. Early length exit leaks only length, which is public knowledge (HMAC-SHA256 hex is always 64 chars). |
| D5 | Empty or whitespace-only secret supplied at creation is stored as `NULL` (= no validation enabled). | An empty-key HMAC is trivially forgeable; storing one would silently enable fake security from an untouched form field. Absent secret must mean "off", deterministically. |
| D6 | Validation ordering inside `ReceiveWebhook`: resolve endpoint (fetching `secret`) → buffer raw body bytes → verify signature → serialize headers → insert. Unknown slug stays `404` before any signature work. | The raw bytes are needed for the HMAC, and buffering is already required for storage, so verification costs no extra pass over the body. Nothing is persisted before verification succeeds. Slug is a public URL component, so resolving first adds no information disclosure beyond status quo. |
| D7 | Migration is additive: `002_endpoint_hmac.sql` adds nullable `endpoints.secret text NULL`. No backfill, no default. | `NULL` maps exactly to "no secret = current behavior", so existing rows and existing deployments keep working unchanged after startup migration. |

## Acceptance criteria

| # | Given | When | Then |
|---|---|---|---|
| AC1 | Endpoint created with secret `S` | Ingest receives valid `X-Webhook-Signature` (`sha256=<hex>` over exact body bytes) | `204`; request persisted with intact body |
| AC2 | Endpoint has secret `S` | Signature present but wrong digest | `401`; feed count unchanged (not persisted) |
| AC3 | Endpoint has secret `S` | Header missing entirely | `401`; not persisted |
| AC4 | Endpoint has secret `S` | Value uses wrong scheme/format (e.g. `sha1=...`, non-hex, wrong length) | `401`; not persisted |
| AC5 | Endpoint has NO secret | Request unsigned (or any signature header present) | `204`, persisted — current behavior unchanged |
| AC6 | `POST /api/endpoints` includes optional `secret` | Success | `201` response contains `secret`; empty/whitespace → stored as NULL, omitted from echo semantics unchanged otherwise |
| AC7 | Any endpoint exists | `GET /api/endpoints` or `GET /api/endpoints/{id}` | Response never contains a `secret` property |
| AC8 | UI form | User leaves "HMAC secret" empty / fills it | Payload carries it optionally; after successful creation the returned secret is shown once, password-style |

## Contract deltas

**API**
- `POST /api/endpoints`: request body gains optional `secret: string`; `201` response gains `secret`.
- `GET /api/endpoints`, `GET /api/endpoints/{id}`: unchanged shapes, guaranteed `secret`-free.
- `POST /hooks/{slug}`: new outcome `401 { error }` when the endpoint has a secret and validation fails; all other outcomes unchanged.
- OpenAPI regenerated → `ui/lib/api-types.ts` gains `secret?: string` on `Request`.

**DB**
- `002_endpoint_hmac.sql`: `ALTER TABLE endpoints ADD COLUMN secret text NULL;`

**UI**
- `EndpointForm`: optional password-style "HMAC secret" input wired into the POST payload; on success shows the returned secret once (read-only password field with copy affordance), cleared before next submission.

## Rollout notes

- Migration applies at startup via the embedded runner; inert until someone sets a secret. No feature flags, no config keys.
- Existing deployments: zero behavioral change until secrets are configured per endpoint.
- Risk: providers signing with timestamp-envelope schemes will get 401s if users configure a secret expecting those schemes — documented in D1 as a non-goal; error message says "Missing or invalid signature" without scheme negotiation.
- Replay re-sends the stored request including its original `X-Webhook-Signature` header; replaying to another environment with a different upstream secret may be rejected there. Out of scope.

## Validation seam

Integration suite (`WebhookReplay.Api.Tests`, Testcontainers + WebApplicationFactory): AC1–AC5 covered by new ingest tests against seeded endpoints with/without secret; AC6–AC7 by create/read-back assertions; AC8 by form wiring (manual/build-level). Manual curl evidence on ports 58xx for 401/204 paths.
