\set ON_ERROR_STOP on

-- The official RSS feed is valid, but article requests currently return HTTP 403
-- to unattended clients. Keep the reviewed source definition available without
-- placing permanently failing items in the analysis queue.
INSERT INTO cti.sources (
    name,
    feed_url,
    allowed_hosts,
    content_selector,
    trust_score,
    enabled
)
VALUES (
    'Dark Reading',
    'https://www.darkreading.com/feeds/rss.xml',
    ARRAY['darkreading.com', 'www.darkreading.com'],
    '.article-content',
    88,
    false
)
ON CONFLICT (name) DO UPDATE
SET feed_url = EXCLUDED.feed_url,
    allowed_hosts = EXCLUDED.allowed_hosts,
    content_selector = EXCLUDED.content_selector,
    trust_score = EXCLUDED.trust_score,
    enabled = false,
    updated_at = now();

INSERT INTO cti.schema_versions (version)
VALUES (24)
ON CONFLICT (version) DO NOTHING;
