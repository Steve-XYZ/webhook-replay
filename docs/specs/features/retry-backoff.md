# Feature: Bounded retries with exponential backoff on replay

Status: shaped, ready to deliver · Owner: this cycle · Slice: stacked on replay-edit (#10)

## Problem

Replay is a single-shot send. One transient blip — connection reset, a stale pod behind the
target's load balancer, a momentary timeout — lands in the audit log as a failed attempt, and
the user must manually click replay again or wrongly conclude the consumer is broken. Debugging
loops want "prove it fails consistently, not once". The fix belongs inside the synchronous
replay call the user is already waiting on, which makes two things non-negotiable: retries must
be strictly bounded (this is a user-facing HTTP request, not a job queue), and every try must be
visible in history exactly like today's attempts.

## Goals

- Configuration: `Retry:MaxAttempts` int default **1** (current behavior, retries OFF) and
  `Retry:BackoffBaseSeconds` int default **1**.
- Delay before retry N+1 = `base * 2^(N-1)` seconds (1s, 2s, 4s, …), each individual wait
  capped at **10s**.
- TOTAL added waiting budget capped at **~15s regardless of configuration**. Replay is a
  synchronous user-facing call: someone is watching a spinner, so no config may turn it into a
  minutes-long hang. The budget caps WAITING, not tries — once the budget is spent, remaining
  tries execute immediately.
- Retry triggers: transport failure/timeout (null status) OR HTTP status >= 500. NEVER on
  success (< 500) and never on 4xx — a 4xx is a definitive answer from the target, retrying it
  would just multiply noise.
- EVERY try records its own `DeliveryAttempt` row carrying the same effective-payload snapshot
  (`request_headers`/`request_body` from replay-edit); sequence is naturally visible via the
  existing attempts listing.
- Final response semantics (explicit):
  - If the LAST try received ANY HTTP response → the normal `200`-style replay response wrapping
    that attempt, **even when the status is 5xx** (identical to today's reachable-target
    semantics; the 5xx is data, not an API error).
  - Only when ALL tries failed at the TRANSPORT level → `502` wrapping the last null-status
    attempt.

## Non-goals

| Non-goal | Rationale |
|---|---|
| Background/scheduled retries | This slice deliberately stays synchronous inside the replay call the user invoked. A durable outbox/job runner changes the product's shape, not its polish. |
| Dead-letter queue | Nothing here outlives the HTTP request; there are no undeliverable messages parked anywhere. |
| Persisting pending retries across restarts | Explicitly deferred with the background-retries goal: process death mid-backoff simply leaves the recorded partial attempts as the audit trail, which is honest. |
| Retrying ingest forwarding (`ReceiveWebhook`) | Ingest is provider-facing fire-and-forget; retry policy there is a separate conversation with different failure economics. |

## Decisions

| # | Decision | Rationale |
|---|---|---|
| D1 | `HandleAsync` delegates to a small `SendWithRetriesAsync` wrapper that loops the existing `SendAndRecordAsync(connection, requestId, effectivePayload, httpClientFactory, ct)` seam once per try. The seam itself keeps ONE responsibility: build + send + record one attempt. | Stacking contract with PR #10 kept intact: one call = one recorded try. The policy lives in the wrapper, so the per-attempt code path PR #10 shaped remains recognizable and untouched in its internals. |
| D2 | `SendAndRecordAsync` returns `(Response, StatusCode?)` instead of bare `IResult`; `StatusCode == null` means transport-level failure. The wrapper decides continuation purely from that pair. | The wrapper needs the outcome, not the HTTP envelope. Returning both avoids re-inspecting `IResult` and keeps "did we talk to the target?" expressible as a nullable int. |
| D3 | Config read once at startup via `AddReplayRetries(this IServiceCollection, IConfiguration)` extension registering a `ReplayRetryOptions(MaxAttempts, BackoffBaseSeconds)` singleton; `MaxAttempts` clamped to `[1, 10]`, `BackoffBaseSeconds` floored at 0. Defaults live in code, not appsettings.json. | Mirrors `AddIngestRateLimiting`/`AddWebhookRetention` exactly (section-style keys + `GetValue` + code defaults; neither of those ships an appsettings section either). The clamp bounds audit-log writes: a typo like `MaxAttempts=10000` cannot spray ten thousand attempt rows against one replay click. |
| D4 | Backoff before retry N+1 = `min(base * 2^(N-1), 10s, remaining budget)`, budget starts at 15s and decrements per wait. | Exponential spacing gives transient blips room to clear while converging fast; the 10s per-wait cap plus 15s global budget make worst-case added latency predictable (~15s) no matter what ops configure. |
| D5 | Outer `cancellationToken` honored during backoff waits: if it fires mid-wait, the loop stops and the ALREADY-RECORDED last attempt's response is returned best-effort. | The client hung up; finishing the loop would waste calls nobody will read. Returning the recorded result preserves the invariant "every completed try is answerable from the response" without throwing past the framework. |
| D6 | Retry predicate: `attemptNumber < MaxAttempts && (statusCode is null || statusCode >= 500)`. Per-attempt 30s send timeout (linked to caller token) unchanged per try. | Precise encoding of the trigger rule. Success and definitive answers (any 2xx–4xx) stop the loop immediately; only "no answer" or "server-side maybe-transient" justify another try. |
| D7 | No schema, endpoint-shape, or UI changes. Multiple attempts surface through the EXISTING attempts listing and the existing response envelope. | The whole feature is observable with zero contract churn because replay-edit already snapshots payloads per attempt and lists them. Consumers see "more rows in history", nothing else. |

## Acceptance criteria

| # | Given | When | Then |
|---|---|---|---|
| AC1 | Default config (no `Retry:*` settings) | Replay against a stub that always returns 500 | Exactly ONE attempt recorded, stub hit once, `200` envelope with `statusCode: 500` — byte-for-byte today's reachable-target behavior |
| AC2 | `Retry:MaxAttempts=3`, stub always returns 500 | Replay | Stub hit exactly 3 times, exactly 3 attempt rows all with `statusCode: 500` and identical effective-payload snapshot, final `200` envelope reflects the LAST attempt (`statusCode: 500`) |
| AC3 | `Retry:MaxAttempts=3`, `Retry:BackoffBaseSeconds=0`, stub returns 500 then 204 | Replay | Loop stops after the success: exactly 2 attempts recorded (one 500, one 204), NO further stub hits, final envelope `statusCode: 204` |
| AC4 | `Retry:MaxAttempts=3`, target unreachable (transport failure every try) | Replay | `502` envelope with null `statusCode`, exactly 3 null-status attempt rows |
| AC5 | `Retry:MaxAttempts=3`, `Retry:BackoffBaseSeconds=1`, always-500 stub | Measure replay wall time | Elapsed >= ~3s (waits of 1s + 2s really happened) and far below any budget-blowing figure; observed values reported, upper bounds sanity-checked manually |
| AC6 | Full solution | `dotnet build` + `dotnet test` | Green including all pre-existing tests unmodified |

## Contract deltas

None. Same endpoints, same request/response shapes, same DB schema. Behaviorally additive only:
with retries enabled, one replay invocation may produce multiple attempt rows and the final
envelope wraps the last try per D6/D7 rules above.

## Rollout notes

- Default OFF (`MaxAttempts=1`): deployments see zero change until they opt in, e.g.
  `Retry__MaxAttempts=3` env var or appsettings section.
- Enabling raises worst-case replay latency to roughly `attempts × 30s` send time plus ≤ 15s
  backoff; operators choosing large `MaxAttempts` opt into that trade-off knowingly.
- Risk: users may expect 5xx replays to return HTTP 502-ish errors; documented here (Goals) that
  a reached target always yields the 200-style envelope — consistent with pre-existing behavior.

## Validation seam

Integration suite (`WebhookReplay.Api.Tests`, Testcontainers + WebApplicationFactory): a
secondary factory instance sharing the fixture's Postgres container overrides `Retry:*` via
`UseSetting`; deterministic counting stub listeners (sequential `HttpListener` accept loops)
serve scripted status sequences. AC1–AC4 covered by four new tests (hit counts, row counts,
status multisets, final envelopes); AC5 lower-bound wall-clock assertion inside the AC2 test
plus reported timings; AC6 by suite greenness.
