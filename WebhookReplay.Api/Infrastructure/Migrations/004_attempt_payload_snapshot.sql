ALTER TABLE delivery_attempts ADD COLUMN request_headers jsonb;
ALTER TABLE delivery_attempts ADD COLUMN request_body text;
