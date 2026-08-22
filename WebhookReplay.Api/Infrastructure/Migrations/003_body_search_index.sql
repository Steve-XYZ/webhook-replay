CREATE EXTENSION IF NOT EXISTS pg_trgm;

CREATE INDEX IF NOT EXISTS idx_webhook_requests_body_text_trgm
    ON webhook_requests USING gin (body_text gin_trgm_ops);
