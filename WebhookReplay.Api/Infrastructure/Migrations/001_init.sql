CREATE TABLE IF NOT EXISTS endpoints (
    id          uuid PRIMARY KEY,
    name        text NOT NULL,
    slug        text NOT NULL UNIQUE,
    forward_url text NOT NULL,
    created_at  timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS webhook_requests (
    id          uuid PRIMARY KEY,
    endpoint_id uuid NOT NULL REFERENCES endpoints(id),
    method      text NOT NULL,
    headers     jsonb NOT NULL,
    body_text   text NOT NULL,
    body_json   jsonb,
    received_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_webhook_requests_endpoint
    ON webhook_requests (endpoint_id, received_at DESC);

CREATE TABLE IF NOT EXISTS delivery_attempts (
    id                 uuid PRIMARY KEY,
    webhook_request_id uuid NOT NULL REFERENCES webhook_requests(id),
    target_url         text NOT NULL,
    status_code        int,
    response_body      text,
    duration_ms        int NOT NULL,
    attempted_at       timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_delivery_attempts_request
    ON delivery_attempts (webhook_request_id, attempted_at DESC);
