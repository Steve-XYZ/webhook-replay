# webhook-replay

Self-hosted tool to **receive, inspect, search and replay webhooks**.

```
POST /hooks/{endpointId}
        ↓
store the full request
        ↓
UI
 ├─ headers
 ├─ body
 ├─ timestamp
 ├─ status
 └─ Replay
        ↓
POST to the configured target
```

The provider fired the event once and you need it again? Replay it — unchanged or against a different environment — instead of reconstructing the payload by hand.

## Stack

- **API:** .NET 10 / ASP.NET Core minimal APIs, raw SQL over Npgsql, PostgreSQL (`jsonb`)
- **UI:** Next.js 16 (App Router) + React 19, zero extra runtime dependencies
- **Infra:** Docker Compose · OpenTelemetry tracing (dev console exporter)

## Quickstart

```bash
git clone https://github.com/Steve-XYZ/webhook-replay.git
cd webhook-replay
docker compose up --build -d
```

| Service | URL |
|---|---|
| UI | http://localhost:3000 |
| API | http://localhost:5000 |

Then:

1. Open http://localhost:3000 and create an endpoint (e.g. slug `test`, forward URL `http://host.docker.internal:5000/hooks/test`).
2. Fire a webhook at it:

```bash
curl -X POST \
  http://localhost:5000/hooks/test \
  -H "Content-Type: application/json" \
  -d '{"orderId":123,"status":"paid"}'
```

3. Watch it appear in the feed, inspect headers/body, hit **Replay**, and check delivery attempts with status codes and durations.

## Local development

```bash
docker compose up -d db                       # Postgres only
dotnet run --project WebhookReplay.Api        # API on :5000
cd ui && npm install && npm run dev           # UI on :3000
```

## API

| Method | Path | Description |
|---|---|---|
| `POST` | `/hooks/{slug}` | Ingest a webhook request |
| `GET` | `/api/endpoints` | List endpoints |
| `POST` | `/api/endpoints` | Create endpoint (`{ name, slug, forwardUrl }`) |
| `GET` | `/api/endpoints/{id}` | Get endpoint |
| `GET` | `/api/endpoints/{id}/webhooks?limit=&before=` | Paginated request feed (keyset via `before`) |
| `GET` | `/api/webhooks/{id}` | Full request detail + attempts |
| `POST` | `/api/webhooks/{id}/replay` | Forward the stored request to its target |
| `GET` | `/api/webhooks/{id}/attempts` | Delivery attempt history |

Configuration overrides: `ConnectionStrings__Default`, `API_BASE` (server-side calls + proxy), `INGEST_BASE` (ingest URLs shown in the UI).

## Project layout

```
WebhookReplay.Api/     ASP.NET Core API (Features/<slice>/ self-contained)
ui/                    Next.js app
docs/specs/            working specs: vision, architecture, data model, slices, roadmap
docker-compose.yml     db + api + ui
```

## Roadmap

Replay with payload editing · retries with backoff · HMAC validation · content search · live SSE feed · retention policies — and the interesting part: a **chaos provider** that simulates bad upstreams (20% → 500, 10% → timeout…) to test the resilience of your integrations. See [docs/specs/05-roadmap-post-mvp.md](docs/specs/05-roadmap-post-mvp.md).

No auth, users, billing, queues or microservices until a real case demands them.
