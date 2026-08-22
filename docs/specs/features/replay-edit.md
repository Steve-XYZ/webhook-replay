# Feature: Replay with payload overrides

Status: shaped, ready to deliver · Owner: this cycle · Slice: post-MVP roadmap item #1 ("Replay con modificación del payload", `05-roadmap-post-mvp.md`)

## Problem

Replay re-sends the webhook exactly as received. The most common debugging loops need the
opposite: fire the *same event shape* against a different target (`localhost` vs staging,
webhook.site, another consumer instance) or a tweaked payload (fix the bug you think you
found and prove the consumer handles it). Today that means copying the stored request into
curl by hand, and — worse — the attempt history records nothing about what was actually
sent, so "what did we deliver?" becomes unanswerable the moment anything varies. Roadmap
already listed this as the #1 post-MVP feature.

## Goals

- `POST /api/webhooks/{id}/replay` accepts an OPTIONAL `application/json` body:
  `{ targetUrl?, headers?, body? }`. Absent or `null` field = fall back to the stored value.
- Fully backward compatible: an empty or missing request body behaves byte-for-byte like
  today's replay; every existing test stays green **unmodified**.
- `targetUrl` override validated with the same rule as forward URLs (absolute http(s)).
- `headers` override REPLACES the stored header set wholesale, using the same
  name→string[] object semantics `ReceiveWebhook` stores.
- `body` override replaces `body_text` raw.
- The EFFECTIVE payload actually sent gets SNAPSHOTTED onto the attempt row — always,
  even when no overrides were supplied — so every future attempt answers "what went out".
- Replay response gains `requestHeaders` / `requestBody` fields (the effective snapshot).

## Non-goals

| Non-goal | Rationale |
|---|---|
| Partial header merge | Merge semantics are where override bugs hide ("why is my auth header still there?"). Wholesale replace is predictable and matches how users think about "send these headers instead". |
| Saving overrides as new stored versions | Overrides are per-attempt intent, not data repair. A "fork this webhook with edits" flow deserves its own slice. |
| Editing attempts history | Attempts are an audit log; the snapshot columns are write-once at insert time. |
| Overriding HTTP method | No debugging scenario needs POST→GET here yet, and it would break the "replay the provider's call" mental model. |

## Decisions

| # | Decision | Rationale |
|---|---|---|
| D1 | The handler binds `HttpRequest` and parses the optional JSON itself: no/empty body → no overrides; malformed JSON or wrong field shapes → `400 { error }`. Not ASP.NET Core inferred model binding. | Inferred body binding returns 400 on empty bodies unless carefully annotated, which risks breaking the byte-for-byte backward-compat contract. Manual parsing makes "absent body" a first-class state and mirrors how `ReceiveWebhook` already reads raw input. |
| D2 | Field-level fallback via C# nullability: absent field and explicit `"field": null` are equivalent and mean "use stored value". `"body": ""` and `"headers": {}` are REAL replacements (send empty). | One rule covers everything: `null = don't touch`. Distinguishing absent vs null buys nothing and complicates the contract; but empty-string-vs-null must differ or users could never clear a body. |
| D3 | Headers override deserialized strictly as `Dictionary<string, string[]>`; non-array values are a 400. Effective header set is either the stored `headers` jsonb verbatim or the serialized override — never mixed. | Exactly the storage format of `ReceiveWebhook`, so `BuildOutgoingRequest` needs zero changes to consume either source. Strictness up front beats silent coercion of `"X-A: v"` into `["v"]`. |
| D4 | `targetUrl` validated with the exact `Uri.TryCreate` absolute-http(s) check used by `CreateEndpoint` (same error message shape). Invalid overrides fail fast with `400`, BEFORE any send and before any attempt row exists. | Same input, same rule, same message — one validation concept in the product. Failing fast keeps failed-validation off the audit log: a 400 means "nothing happened", which is true. |
| D5 | Migration `004_attempt_payload_snapshot.sql` adds nullable `delivery_attempts.request_headers jsonb NULL` and `request_body text NULL`; no backfill, no default. New inserts populate BOTH columns ALWAYS (effective values even with no overrides), on the success AND the network-failure path. | Additive nullable columns keep old rows and old deployments working untouched; `NULL` reads as "recorded before snapshots existed". Always-populate makes the invariant simple: every post-upgrade attempt fully describes its delivery. |
| D6 | Replay responses (200 and 502 alike) gain `requestHeaders` (object name→string[]) and `requestBody` (string) mirroring the snapshot. `GET .../attempts` list shape stays unchanged. | The caller of replay wants immediate confirmation of what was sent without a DB query; the list endpoint feeds an existing UI banner whose shape we promised not to churn. Old consumers ignore two added fields. |
| D7 | `ReplayWebhook.HandleAsync` decomposes into: load stored → resolve overrides → build `EffectivePayload` (method, url, headers, body) → `SendAndRecordAsync(...)` (build outgoing → send → read → insert attempt / record failure). All steps below `HandleAsync` are private methods over `EffectivePayload`. | Stacking contract: the retry-backoff branch builds on this one and needs to re-invoke exactly the send-and-record step per retry. One seam, one parameter type — no monolith to untangle later. `BuildOutgoingRequest` consumes the effective payload cleanly because construction never mixes stored and override sources. |
| D8 | Snapshot serialization reuses the in-memory effective header element (`JsonSerializer.Serialize(JsonElement)`); no second source of truth for "effective". | The DB column and the API response are rendered from the same value the outgoing request was built from — the snapshot cannot drift from what was sent. |

## Acceptance criteria

| # | Given | When | Then |
|---|---|---|---|
| AC1 | Stored webhook, endpoint pointing at reachable stub | Replay with NO body (legacy callers) | `200`, original body arrives at ORIGINAL url byte-for-byte; response now includes `requestHeaders`/`requestBody` equal to stored values; all existing tests pass unmodified |
| AC2 | Stored webhook ingested as JSON | Replay with `{ "targetUrl": "<stub B>", "body": "new-body" }` | Stub B receives NEW body at NEW path; response `statusCode` 2xx-ish per stub; `requestBody` == `"new-body"`; `requestHeaders` reflect the stored ingest headers; snapshot persisted (asserted via response fields) |
| AC3 | Stored webhook | Replay with `{ "headers": { "X-Custom": ["v"] } }` | Outgoing request carries ONLY `X-Custom` (+ transport-managed Host/Content-Length exclusions as today) — stored headers gone wholesale |
| AC4 | Stored webhook | Replay with `{ "body": "x" }` only | Original URL used; with `{ "headers": {...} }` only, original body used (per-field fallback) |
| AC5 | Any stored webhook | Replay with `{ "targetUrl": "not-a-url" }` (or relative / ftp) | `400` with absolute-http(s) error; no outbound request; attempts count unchanged |
| AC6 | Pre-migration attempt row exists | After upgrade, inspect rows | Old row keeps `request_headers`/`request_body` NULL; every NEW attempt has both populated regardless of overrides |
| AC7 | Webhook detail page | User opens collapsible "Replay with overrides", fills optional targetUrl/body, submits | Same endpoint called with JSON body; result banner behaves exactly as the plain Replay button; attempts refresh |
| AC8 | Full solution | `dotnet build` + `dotnet test` | Green; baseline 5 tests still pass plus new coverage for AC1–AC5 |

## Contract deltas

**API**
- `POST /api/webhooks/{id}/replay`: optional JSON body `{ targetUrl?: string, headers?: Record<string, string[]>, body?: string }`.
  - New outcomes: `400 { error }` for malformed JSON body or invalid `targetUrl`/`headers` shapes. Everything else unchanged.
- Replay responses (200/502): gain `requestHeaders: Record<string, string[]>`, `requestBody: string`.

**DB**
- `004_attempt_payload_snapshot.sql`: `ALTER TABLE delivery_attempts ADD COLUMN request_headers jsonb NULL; ADD COLUMN request_body text NULL;`

**UI**
- `AttemptsPanel`: collapsible "Replay with overrides" region (optional target URL input + optional raw body textarea) posting `{ targetUrl?, body? }` to the same endpoint; existing Replay button and result banner untouched.

## Rollout notes

- Migration applies at startup via the embedded runner; inert until someone sends an override.
- Existing deployments/callers: zero behavioral change (D1/D5); the only visible delta is two extra response fields.
- Risk: users may expect header MERGE semantics; the 400-free wholesale replace is documented here and the UI labels the field accordingly.

## Validation seam

Integration suite (`WebhookReplay.Api.Tests`, Testcontainers + WebApplicationFactory):
AC1–AC5 covered by new replay tests using capturing stub listeners (asserting received path +
body) and response snapshot fields; AC6 by the always-populate insert paths (NULL only possible
for legacy rows — asserted implicitly by new-row population). AC7 manual/build-level.
Manual curl evidence on spare ports 58xx: end-to-end override replay captured against a local
stub listener.
