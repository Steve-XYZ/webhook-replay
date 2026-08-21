# Roadmap post-MVP

Ordenado por valor/riesgo. Nada de esto entra hasta que el MVP exista y funcione end-to-end.

## Fase 1 — Herramienta seria

| # | Feature | Notas |
|---|---|---|
| 1 | Replay con modificación del payload | Editar body/headers antes de reenviar; el attempt registra el payload efectivo |
| 2 | Retries con exponential backoff | Primer trabajo asíncrono real; decidir worker in-process vs proceso aparte |
| 3 | HMAC signature validation | Validar firma entrante por endpoint (secreto en `endpoints`) |
| 4 | Búsqueda por contenido | Índice GIN sobre `body_json`; luego full-text |
| 5 | SSE tiempo real | Feed de webhooks entrando en vivo en la UI |
| 6 | Rate limiting + retention policies | Proteger la BD; borrar requests viejos por endpoint |

## Fase 2 — Producto

- Dead-letter queue (requests que agotan retries)
- Diff entre entregas (comparar dos attempts lado a lado)
- Filtering con JSONPath (reenviar solo si el payload cumple una condición)
- Endpoint temporal público vía tunnel
- Export/import de endpoints y requests
- CLI (crear endpoints, replay desde terminal)
- SDKs
- Equipos y permisos

## El punto interesante — Chaos Provider

Módulo para simular proveedores malos y probar la resiliencia de integraciones. Un endpoint "destino" configurable con distribución de fallos:

```
20% → 500
10% → timeout de 15 s
 5% → response malformado
65% → 200
```

### Por qué importa

Convierte a Webhook Replay en herramienta de **resiliencia testing**: apuntas tu consumidor de webhooks contra un destino caótico y verificas que tus retries, timeouts, circuit breakers y alertas se comporten. Es lo que separa este proyecto de otro inspector CRUD.

### Diseño tentativo

- Nuevo modelo: `ChaosProfile` ligado a un endpoint destino (porcentajes por tipo de fallo, seed determinista opcional).
- Determinismo con seed → mismo escenario reproducible en CI.
- Métricas OTel por tipo de fallo servido.
