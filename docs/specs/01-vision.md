# Webhook Replay — Visión

Herramienta **self-hosted** para recibir, inspeccionar, buscar y reejecutar webhooks.

Sin relación con loterías ni ningún otro dominio. Es pequeña para empezar hoy, pero tiene recorrido real como producto de infraestructura de desarrolladores.

## Problema

Depurar integraciones de webhooks es doloroso: el proveedor dispara el evento una sola vez, no tienes el payload original, y reproducirlo requiere reconstruirlo a mano. Webhook Replay captura cada request completo, lo guarda, y permite reenviarlo cuantas veces haga falta contra cualquier destino.

## Qué hace (MVP)

```
POST /hooks/{endpointId}
        ↓
guardar request completo
        ↓
UI
 ├─ headers
 ├─ body
 ├─ timestamp
 ├─ status
 └─ Replay
        ↓
POST al destino configurado
```

## Qué NO hace (todavía)

Fuera de alcance explícito del MVP:

- Auth, usuarios, billing
- Kafka / colas / microservicios
- Abstractions genéricas (repository patterns, CQRS frameworks, plugins)

Cada uno de estos entra solo cuando un caso real lo exija.

## El punto diferencial (post-MVP)

Simular proveedores malos: respuestas 500, timeouts largos, payloads malformados, con distribución configurable (p. ej. 20% → 500, 10% → timeout 15 s, 5% → respuesta malformada, 65% → 200).

Con eso deja de ser un inspector de webhooks y se convierte en una herramienta para **probar la resiliencia de integraciones**. Ese es el terreno donde tiene más sustancia técnica que otro CRUD.

## Primer objetivo verificable

```bash
curl -X POST \
  http://localhost:5000/hooks/test \
  -H "Content-Type: application/json" \
  -d '{"orderId":123,"status":"paid"}'
```

…y que el request quede persistido en PostgreSQL. Todo lo demás se construye sobre esa base.
