#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."

port="${OPENAPI_PORT:-5099}"
base_url="http://localhost:${port}"
log="$(mktemp)"

dotnet build WebhookReplay.Api --nologo -v quiet

setsid dotnet run --project WebhookReplay.Api --no-build --urls "$base_url" >"$log" 2>&1 &
app_pid=$!

cleanup() {
  kill -- "-$app_pid" 2>/dev/null || true
  wait "$app_pid" 2>/dev/null || true
  rm -f "$log"
}
trap cleanup EXIT

ready=false
for _ in $(seq 1 60); do
  if curl -fs -o /dev/null "$base_url/openapi/v1.json"; then
    ready=true
    break
  fi
  if ! kill -0 "$app_pid" 2>/dev/null; then
    echo "error: la API salió antes de servir OpenAPI; log:" >&2
    cat "$log" >&2
    exit 1
  fi
  sleep 1
done

if [ "$ready" != true ]; then
  echo "error: timeout esperando $base_url/openapi/v1.json; log:" >&2
  cat "$log" >&2
  exit 1
fi

curl -fsS "$base_url/openapi/v1.json" -o openapi.json
echo "openapi.json generado ($(wc -c < openapi.json) bytes)"
