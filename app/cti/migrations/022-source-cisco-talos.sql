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
    'Cisco Talos',
    'https://blog.talosintelligence.com/rss/',
    ARRAY['blog.talosintelligence.com'],
    '.post-content',
    92,
    true
)
ON CONFLICT (name) DO UPDATE
SET feed_url = EXCLUDED.feed_url,
    allowed_hosts = EXCLUDED.allowed_hosts,
    content_selector = EXCLUDED.content_selector,
    trust_score = EXCLUDED.trust_score,
    updated_at = now();

INSERT INTO cti.schema_versions (version)
VALUES (22)
ON CONFLICT (version) DO NOTHING;
