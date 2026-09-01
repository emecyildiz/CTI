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
    'BleepingComputer',
    'https://www.bleepingcomputer.com/feed/',
    ARRAY['bleepingcomputer.com', 'www.bleepingcomputer.com'],
    '.articleBody',
    85,
    true
)
ON CONFLICT (name) DO UPDATE
SET feed_url = EXCLUDED.feed_url,
    allowed_hosts = EXCLUDED.allowed_hosts,
    content_selector = EXCLUDED.content_selector,
    trust_score = EXCLUDED.trust_score,
    updated_at = now();

INSERT INTO cti.schema_versions (version)
VALUES (21)
ON CONFLICT (version) DO NOTHING;
