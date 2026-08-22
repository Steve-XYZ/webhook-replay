# Feature: Content search over webhook bodies

Status: shaped, ready to deliver · Owner: this cycle · Slice: post-MVP roadmap item #4 (`05-roadmap-post-mvp.md`)

## Problem

An endpoint feed only offers keyset paging (`before`/`limit`). Once a provider has fired hundreds of requests at one slug, finding "the webhook where order X failed" means eyeballing 200-char previews page by page. Debugging almost always starts from a known payload fragment — an event name, a SKU, an error string — so substring match over what the provider actually sent is the shortest path from symptom back to request.

## Goals

- Case-insensitive substring search over stored request bodies (`body_text`) within one endpoint's feed.
- Optional `q` query parameter on `GET /api/endpoints/{endpointId}/webhooks` that composes with the existing keyset pagination instead of replacing it.
- Search stays index-backed as feeds grow; no per-query sequential scan over all captured bodies.
- User input is literal: `%`, `_` and `\` inside `q` never act as wildcards.
- UI: search box on the endpoint page; typing refetches page 1 with the query; "Load older" keeps the active query; clearing restores the unfiltered view.

## Non-goals

| Non-goal | Rationale |
|---|---|
| Full-text ranking / tsvector relevance | Debugging lookups are exact-fragment hunts ordered by recency, not ranked retrieval. Relevance adds language/stemming configuration with no current user need; revisit when time-ordered results feel noisy enough to want them. |
| Header search | Providers put identity in payloads far more often than headers; per-request headers are already visible in the detail view. |
| Cross-endpoint global search | Feeds are per-endpoint today; global results would need endpoint-attributed UI and its own navigation story. |
| jsonb operator filtering (`@>`, `?`) | Requires knowing each provider's payload shape up front; substring works on every body regardless of structure. The roadmap sketch ("GIN sobre `body_json`") serves structured filtering, not fragment lookup — see D1/D2 for why `body_text` trigram wins here. |

## Decisions

| # | Decision | Rationale |
|---|---|---|
| D1 | Match = case-insensitive substring of `body_text`, implemented as `body_text ILIKE '%' \|\| @q \|\| '%'`. Bodies only — not headers, not jsonb operators. | This is a debugging tool: users paste a fragment visible in a preview or known from provider docs. Substring needs zero schema knowledge and behaves identically for JSON and non-JSON bodies. Headers are small, structured and already inspectable per request; jsonb operators would fork behavior between rows with `body_json` set and null. |
| D2 | Migration `003_body_search_index.sql`: `CREATE EXTENSION IF NOT EXISTS pg_trgm;` plus GIN index `ON webhook_requests USING gin (body_text gin_trgm_ops)`. Additive, idempotent, applied by the embedded runner like every other migration. | Why trgm-GIN keeps ILIKE indexed at scale: a b-tree cannot serve `%term%` because a leading-wildcard pattern is not a contiguous key range, so Postgres degrades to scanning the endpoint's rows. pg_trgm decomposes text — and the LIKE pattern itself — into overlapping 3-character shingles; GIN stores an inverted posting list per shingle, so `%invoice.paid%` becomes "intersect postings for `inv`, `nvo`, `voi`, …" resolved by bitmap index scan. Patterns ≥ 3 characters get real selectivity; shorter ones fall back to a cheap post-filter. GIN also absorbs ingest inserts and retention deletes without manual upkeep. |
| D3 | Wildcard neutralization happens in application code before parameterization: escape `\` first, then `%` and `_`; wrap the escaped value in `%…%` at SQL level; rely on Postgres' default LIKE escape character (backslash). `q` reaches SQL only as parameter `@q`, never by string concatenation. | Escaping after interpolation would be too late and injection-prone; one helper in C# is a single testable seam. The default escape character means no `ESCAPE` clause noise. Integration tests pin literal treatment of `%`, `_` and `\` so a regression fails loudly rather than silently returning wrong rows. |
| D4 | Filter composes additively with keyset pagination: append `AND (@q IS NULL OR body_text ILIKE '%' \|\| @q \|\| '%')` to the existing predicate; empty/whitespace `q` normalizes to `NULL`. | One code path for filtered and unfiltered reads. `nextBefore` stays "received_at of the last returned row", so "Load older" carries `q` along and continues the same cursor window without special cases. |
| D5 | Migration numbered exactly `003_body_search_index.sql` — 002 belongs to hmac-validation, 004 to replay-edit. Both statements are idempotent (`IF NOT EXISTS`); `CREATE EXTENSION` is safe inside the runner's per-file transaction. | Parallel branches own adjacent numbers; colliding or reordering numbers would make merged history apply migrations in a different order than any branch tested. The ordinal filename sort in the runner makes 001→003 deterministic. |
| D6 | UI state lives in `WebhookFeed`: input value + debounced (~300 ms) refetch of page 1 through a shared URL builder that attaches `q` and/or `before`; Refresh also preserves the active query. No route or searchParams changes. | The component already owns all fetch/pagination state; routing typing, Refresh and Load older through one URL builder keeps them consistent by construction instead of three copies of query-string logic. Debounce spares the API per keystroke while keeping the interaction responsive. |

## Acceptance criteria

| # | Given | When | Then |
|---|---|---|---|
| AC1 | Endpoint has two requests with different bodies | `GET …/webhooks?q=` with a fragment unique to one | Exactly the matching request is returned; response shape unchanged |
| AC2 | Feed has ≥3 requests spanning a page boundary | `q` supplied together with a `before` cursor | Only matching rows older than the cursor return — filter AND cursor compose |
| AC3 | Bodies contain literal `%`, `_`, `\` sequences | `q` contains those same characters | Only bodies containing the literal characters match; no wildcard expansion |
| AC4 | No `q` supplied (or empty/whitespace) | Any list request | Behavior identical to today; existing pagination tests keep passing |
| AC5 | Fresh database | Startup runner applies 001→003 | `pg_trgm` extension exists and the GIN index exists on `webhook_requests(body_text)`; re-running startup is a no-op |
| AC6 | Feed rendered with captured requests | User types a fragment | Page 1 refetched with `q` (debounced); items and count reflect the filter |
| AC7 | Active query | User clicks "Load older" | Older page requested with same `before` AND same `q` |
| AC8 | Active query | User clears the input | Unfiltered page 1 restored |

## Contract deltas

**API**
- `GET /api/endpoints/{endpointId}/webhooks`: gains optional `q: string` query parameter. Unknown endpoint still 404s; all other outcomes and response shapes unchanged. OpenAPI regenerated → `ui/lib/api-types.ts`.

**DB**
- `003_body_search_index.sql`: `CREATE EXTENSION IF NOT EXISTS pg_trgm;` + `CREATE INDEX IF NOT EXISTS idx_webhook_requests_body_text_trgm ON webhook_requests USING gin (body_text gin_trgm_ops);`

**UI**
- `WebhookFeed` gains a search input above the feed; no new routes or pages.

## Rollout notes

- Inert until someone types a query: no feature flags, no config keys. Unfiltered reads run today's SQL plus one always-true predicate.
- Risk: managed Postgres flavors gate `CREATE EXTENSION` behind elevated roles (RDS grants it to `rds_superuser`; Cloud SQL similar), while self-hosted images ship `pg_trgm` in contrib. If the privilege is missing, startup fails loudly rather than silently searching unindexed — acceptable for a self-hosted tool.
- Queries under 3 characters cannot form trigrams and fall back to scan-and-filter; acceptable for interactive per-endpoint feeds.

## Validation seam

Integration suite (`WebhookReplay.Api.Tests`, Testcontainers postgres:17): the fixture boots the app against a fresh database, so the embedded runner applies 001–003 before any test — AC1–AC5 are exercised against the real migrated schema (extension + index present during tests). AC6–AC8 are wired in `WebhookFeed` and validated by ui lint/typecheck/build plus a manual pass on spare ports (58xx).
