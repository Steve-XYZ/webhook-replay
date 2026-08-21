# Vertical slices — MVP

Orden de entrega. Cada slice es un incremento funcional completo (ruta HTTP + persistencia + verificación). Sin capas compartidas por adelantado.

## Slice 0 — Bootstrap ✅

- [x] Solución `WebhookReplay.slnx` + proyecto `WebhookReplay.Api` (net10.0)
- [x] Git inicializado, commit `chore: bootstrap webhook replay`
- [x] Specs escritos

## Slice 1 — ReceiveWebhook ✅

**Ruta:** `POST /hooks/{slug}`

Persiste method, headers y body en `webhook_requests`, resolviendo el endpoint por slug.

**Criterios de aceptación:**
1. El objetivo raíz funciona:
   ```bash
   curl -i -X POST \
     http://localhost:5000/hooks/test \
     -H "Content-Type: application/json" \
     -d '{"orderId":123,"status":"paid"}'
   ```
2. La fila aparece en Postgres con body intacto y `received_at` correcto.
3. Slug inexistente → `404`. Body vacío o no-JSON → se guarda igual (`body_json = NULL`).
4. Responde rápido (sin forward síncrono en esta slice).

**Setup requerido:** puerto HTTP fijo en 5000; Postgres vía Docker Compose; migraciones aplicadas al arrancar.

## Slice 2 — CreateEndpoint

**Ruta:** `POST /api/endpoints` `{ name, slug, forwardUrl }`

**Criterios de aceptación:**
1. Crea fila en `endpoints`; responde `201` con el recurso.
2. Slug duplicado → `409`.
3. Validación: slug no vacío (patrón `[a-z0-9-]+`), `forwardUrl` debe ser URL absoluta http(s).
4. Con esta slice, el flujo end-to-end ya no depende de SQL manual: se crea el endpoint `test` vía API y se le dispara el curl de la slice 1.

## Slice 3 — GetEndpoint

**Ruta:** `GET /api/endpoints/{id}`

**Criterios de aceptación:** `200` con datos del endpoint; id inexistente → `404`.

## Slice 4 — ListWebhooks

**Ruta:** `GET /api/endpoints/{endpointId}/webhooks?limit=&before=`

Lista paginada simple (más reciente primero) para la UI.

**Criterios de aceptación:**
1. Devuelve id, method, timestamp y preview del body por request.
2. Default `limit=50`, máximo 100.
3. Orden estable por `received_at DESC`.

## Slice 5 — GetWebhook

**Ruta:** `GET /api/webhooks/{id}`

Detalle completo: headers, body crudo, parsed JSON si aplica, timestamp, y delivery attempts existentes.

**Criterios de aceptación:** headers/body exactamente como fueron recibidos (byte-fiel); incluye attempts ordenados por `attempted_at DESC`.

## Slice 6 — ReplayWebhook

**Ruta:** `POST /api/webhooks/{id}/replay`

Reenvía el request guardado al `ForwardUrl` del endpoint y registra el `DeliveryAttempt`.

**Criterios de aceptación:**
1. El destino recibe POST con mismo método, headers y body original.
2. Se registra attempt con status code, response body (truncado a 64 KB), duración y timestamp.
3. Timeout del destino → attempt queda con `status_code = NULL` y la API responde `502` con el attempt serializado.
4. Re-ejecutable N veces; cada intento crea una fila nueva.

## Slice 7 — GetDeliveryAttempts

**Ruta:** `GET /api/webhooks/{id}/attempts`

Historial de intentos de un request.

**Criterios de aceptación:** lista completa por request, más reciente primero; alimenta el "status" que muestra la UI.

## Después del MVP (UI)

Con las 7 slices la API está completa. La UI (Next.js) consume todo: lista de endpoints, feed de requests, detalle con headers/body/status, botón **Replay**. Ver `05-roadmap-post-mvp.md`.
