\set ON_ERROR_STOP on

INSERT INTO cti.sources (
    name,
    feed_url,
    allowed_hosts,
    content_selector,
    trust_score,
    enabled
)
VALUES (
    'Krebs on Security',
    'https://krebsonsecurity.com/feed/',
    ARRAY['krebsonsecurity.com', 'www.krebsonsecurity.com'],
    '.entry-content',
    90,
    true
)
ON CONFLICT (name) DO UPDATE
SET feed_url = EXCLUDED.feed_url,
    allowed_hosts = EXCLUDED.allowed_hosts,
    content_selector = EXCLUDED.content_selector,
    trust_score = EXCLUDED.trust_score,
    enabled = EXCLUDED.enabled,
    updated_at = now();

INSERT INTO cti.schema_versions (version)
VALUES (23)
ON CONFLICT (version) DO NOTHING;
