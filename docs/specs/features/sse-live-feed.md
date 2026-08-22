# Feature: SSE live feed

## Problem

The endpoint page is pull-only: after landing on it, new webhook requests appear only when
the user clicks Refresh or reloads. During integration debugging you sit on the feed and
fire requests from another system — waiting for them to show up manually defeats the point.
The roadmap already listed "SSE tiempo real" as post-MVP item #5.

## Goals

- New webhooks for an endpoint appear in the UI feed without any user action.
- The API exposes one streaming endpoint: `GET /api/endpoints/{endpointId}/events`
  (`text/event-stream`), publishing events with the same shape as list items
  (`id`, `method`, `receivedAt`, `bodyPreview`) so the UI can merge both sources.
- Publish happens only after the row is successfully inserted, so a live event never
  announces data that isn't stored.
- Connection lifecycle is explicit: 404 for unknown endpoints, heartbeat comment every
  ~15 s so proxies don't idle-timeout, clean teardown on `RequestAborted`, and no leaked
  per-endpoint state once the last subscriber leaves.
- The UI degrades gracefully: if the connection drops, `EventSource` auto-reconnects and
  the user just sees the dot turn off/on — never an error banner for a flaky stream.

## Non-goals (accepted for now)

| Non-goal | Rationale |
|---|---|
| Multi-instance fanout (Redis/pubsub) | webhook-replay runs as a single instance today. Broadcast state lives in process memory; scaling out would require an external bus. Accepted limitation until there is a real deployment that needs it. |
| Auth on the SSE endpoint | No other route has auth; this adds none either. |
| Replay / mutation actions over SSE | Stream is read-only telemetry. Replay stays a POST command. |
| Backpressure guarantees | Slow clients drop oldest buffered frames instead of blocking ingest; the initial page fetch covers reconciliation. |

## Decisions

| # | Decision | Rationale |
|---|---|---|
| D1 | In-memory broadcast hub in `Features/Webhooks/LiveFeed.cs`: static `ConcurrentDictionary<Guid, LiveFeedGroup>` keyed by `endpointId`; each group holds one bounded channel (`DropOldest`, capacity 64) **per subscriber**. | Matches the single-instance architecture ("sin capas ni abstractions cruzadas"). One shared channel per endpoint would make concurrent tabs compete consumers and silently lose events; per-subscriber channels keep broadcast semantics at ~zero cost for a self-hosted tool. |
| D2 | `ReceiveWebhook` publishes after `ExecuteNonQueryAsync` succeeds, building the JSON inline from values it already has (hoisted `receivedAt`, truncated `bodyPreview`). | Guarantees store-then-notify ordering with no transactional coupling. Publishing uses `TryWrite`, so a full/broken subscriber can never fail ingest. |
| D3 | SSE framing: `retry:` hint + `: connected` comment at start, `event: webhook` + `id: {webhookId}` + `data: {json}` per event, `\n\n` terminator, heartbeat as comment lines (`: heartbeat`). | `event:` name lets the UI ignore heartbeats for free; `id:` enables future `Last-Event-ID` replay; comments are the standard keepalive that keeps intermediaries from buffering/closing idle streams. |
| D4 | Heartbeat every ~15 s via `Task.Delay` racing `WaitToReadAsync` on a linked CTS. | Simple, allocation-light loop; cancellation of the losing task keeps nothing pending past abort. |
| D5 | Endpoint validates existence against Postgres before subscribing and returns 404 otherwise. | Same contract as `/webhooks` list; prevents unbounded channel growth for garbage ids since groups are created only after validation. |
| D6 | Group removal happens when its last subscriber unsubscribes. | No state leak across browsing sessions; next subscriber recreates the group. |
| D7 | UI subscribes via relative-path `EventSource` (Next rewrites already proxy `/api/:path*`), prepends live items deduplicating by id against existing state, cleans up with `source.close()` on unmount, tracks open/error only to drive a small live-indicator dot. | Zero new dependencies (project constraint); dedupe makes reconnects and manual refresh idempotent; errors are surfaced as "dot off", not scary banners. |

## Acceptance criteria

| # | Criterion | Verified by |
|---|---|---|
| AC1 | `GET /api/endpoints/{endpointId}/events` responds `200` with `Content-Type: text/event-stream`. | Integration test + E2E curl |
| AC2 | Unknown or malformed `endpointId` → `404`, same error shape as sibling endpoints. | Integration test |
| AC3 | A webhook posted to `/hooks/{slug}` produces exactly one SSE event whose `data` matches `{id, method, receivedAt, bodyPreview}` of the stored row, framed as `event: webhook` / `id:` / `data:` + blank line. | E2E curl capture + integration test |
| AC4 | Event is published only after successful insert (a stored request always has a matching live event; failed inserts produce none). | Code path: publish follows `ExecuteNonQueryAsync` |
| AC5 | Heartbeat comment arrives within ~15–16 s on an idle stream; stream stays open. | E2E curl with `--max-time` > 15 s |
| AC6 | Client disconnect (`RequestAborted`) ends the handler without exceptions; last unsubscribe removes the endpoint's group. | Handler structure + finally-block teardown; covered by test client closing streams |
| AC7 | UI subscribes on mount, closes on unmount, prepends live items deduplicated by id, shows a live indicator while connected. | Component implementation |
| AC8 | Existing behavior unchanged: build green, full `dotnet test` suite passes. | CI run |

## Validation evidence

Scripted E2E (API on :5804, `curl -N --max-time 12` on the events stream, then a webhook
POST) captures the raw SSE output including the delivered event frame — see PR description.
