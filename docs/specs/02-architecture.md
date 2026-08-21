# Arquitectura

## Stack

| Capa | Tecnología |
|---|---|
| API | .NET 10 + ASP.NET Core (minimal APIs) |
| Persistencia | PostgreSQL (`jsonb` para headers y body) |
| UI | React / Next.js |
| Infra local | Docker Compose (API, Postgres, UI) |
| Observabilidad | OpenTelemetry (traces de receive → replay) |

## Estructura de la solución

```
webhook-replay/
├── WebhookReplay.slnx
├── WebhookReplay.Api/          # host ASP.NET Core único: endpoints + slices
│   └── Features/               # una carpeta por slice vertical
│       ├── Endpoints/
│       │   ├── CreateEndpoint/
│       │   └── GetEndpoint/
│       ├── Webhooks/
│       │   ├── ReceiveWebhook/
│       │   ├── GetWebhook/
│       │   └── ListWebhooks/
│       └── Deliveries/
│           ├── ReplayWebhook/
│           └── GetDeliveryAttempts/
├── docs/specs/                 # specs de trabajo
└── docker-compose.yml          # Postgres primero; API/UI después
```

Una sola API por ahora. Las slices viven en carpetas autocontenidas (handler, request/response, persistencia) — sin capas ni abstractions cruzadas. Si dos slices comparten código, se extrae en ese momento, no antes.

## Flujo del MVP

1. **Receive** — `POST /hooks/{slug}` captura method, headers, body crudo y timestamp; responde `204` rápido.
2. **Inspect** — la UI lista requests por endpoint y muestra el detalle completo leyendo de Postgres.
3. **Replay** — reenvía el request guardado al `ForwardUrl` del endpoint; registra cada intento con status code, respuesta y duración.

## Decisiones

- **Body se guarda crudo además de parseado.** El body original byte a byte es la fuente de verdad para replay fiel; el `jsonb` solo facilita búsqueda.
- **Replay síncrono en el MVP.** Sin workers ni colas. Los retries con backoff llegan después.
- **Puerto HTTP fijo en 5000** (`launchSettings` trae 5112 por defecto): los ejemplos y tests apuntan a `http://localhost:5000`.
- **OpenTelemetry desde el inicio pero mínimo:** traces con un span por receive y uno por delivery attempt. Sin dashboards todavía.

## Docker Compose (fase inicial)

```yaml
# servicios: db (postgres:17), api (dotnet run), ui (next dev)
# la API espera a Postgres antes de aplicar migraciones
```

Migraciones con EF Core o scripts SQL planos — decidir al construir la primera slice que toca BD.
