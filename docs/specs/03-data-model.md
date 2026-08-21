# Modelo de datos inicial

Tres tablas. Sin soft-deletes, sin auditoría, sin columnas "por si acaso".

## Endpoint

Un punto de recepción con un destino de reenvío.

| Columna | Tipo | Notas |
|---|---|---|
| `id` | `uuid` | PK, generado por la app |
| `name` | `text` | nombre legible |
| `slug` | `text` | **unique** — forma parte de la URL pública `/hooks/{slug}` |
| `forward_url` | `text` | destino del replay |
| `created_at` | `timestamptz` | UTC |

## WebhookRequest

Cada request recibido en un endpoint.

| Columna | Tipo | Notas |
|---|---|---|
| `id` | `uuid` | PK |
| `endpoint_id` | `uuid` | FK → endpoint.id |
| `method` | `text` | normalmente POST, se guarda el real |
| `headers` | `jsonb` | headers tal como llegaron |
| `body_text` | `text` | body crudo original (fuente de verdad para replay) |
| `body_json` | `jsonb` | parseo si es JSON válido; `NULL` si no lo es (para búsqueda) |
| `received_at` | `timestamptz` | UTC |

## DeliveryAttempt

Cada intento de reenvío (incluye el replay manual).

| Columna | Tipo | Notas |
|---|---|---|
| `id` | `uuid` | PK |
| `webhook_request_id` | `uuid` | FK → webhook_request.id |
| `target_url` | `text` | URL efectiva usada en este intento |
| `status_code` | `int` | `NULL` = no hubo respuesta (timeout / error de red) |
| `response_body` | `text` | truncado a un límite fijo (p. ej. 64 KB) |
| `duration_ms` | `int` | medido alrededor del HTTP call |
| `attempted_at` | `timestamptz` | UTC |

## SQL de referencia

```sql
CREATE TABLE endpoints (
    id          uuid PRIMARY KEY,
    name        text NOT NULL,
    slug        text NOT NULL UNIQUE,
    forward_url text NOT NULL,
    created_at  timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE webhook_requests (
    id         uuid PRIMARY KEY,
    endpoint_id uuid NOT NULL REFERENCES endpoints(id),
    method     text NOT NULL,
    headers    jsonb NOT NULL,
    body_text  text NOT NULL,
    body_json  jsonb,
    received_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX idx_webhook_requests_endpoint
    ON webhook_requests (endpoint_id, received_at DESC);

CREATE TABLE delivery_attempts (
    id                 uuid PRIMARY KEY,
    webhook_request_id uuid NOT NULL REFERENCES webhook_requests(id),
    target_url         text NOT NULL,
    status_code        int,
    response_body      text,
    duration_ms        int NOT NULL,
    attempted_at       timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX idx_delivery_attempts_request
    ON delivery_attempts (webhook_request_id, attempted_at DESC);
```

## Notas

- Los índices cubren los dos listados del MVP: requests recientes por endpoint y attempts por request.
- `body_json` es derivable de `body_text`; si algún día pesa el almacenamiento, se puede mover a columna generada o descartar.
- Límite de tamaño de body entrante: fijar uno explícito (p. ej. 1 MB) y responder `413` arriba de eso.
