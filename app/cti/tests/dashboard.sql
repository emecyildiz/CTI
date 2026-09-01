\set ON_ERROR_STOP on

INSERT INTO cti.articles (
    canonical_url,
    title,
    normalized_title,
    title_hash,
    category,
    severity,
    summary_tr,
    published_at,
    analyzed_at
)
VALUES (
    'https://thehackernews.com/example-dashboard-test',
    'Example vulnerability intelligence record',
    'example vulnerability intelligence record',
    encode(digest('example vulnerability intelligence record', 'sha256'), 'hex'),
    'vulnerability',
    'high',
    'Bu kayıt yalnızca özel CTI panelinin geçici entegrasyon testinde kullanılır.',
    now() - interval '2 hours',
    now() - interval '1 hour'
)
ON CONFLICT (canonical_url) DO NOTHING;

INSERT INTO cti.article_occurrences (
    article_id,
    source_id,
    original_url,
    source_title,
    source_published_at
)
SELECT
    article.id,
    source.id,
    article.canonical_url,
    article.title,
    article.published_at
FROM cti.articles AS article
CROSS JOIN cti.sources AS source
WHERE article.canonical_url = 'https://thehackernews.com/example-dashboard-test'
  AND source.name = 'The Hacker News'
ON CONFLICT DO NOTHING;

INSERT INTO cti.reports (
    report_type,
    window_start,
    window_end,
    status,
    title,
    content,
    sent_at
)
VALUES (
    'weekly',
    now() - interval '7 days',
    now(),
    'sent',
    'Example weekly assessment',
    'This report exists only in the temporary dashboard integration test.',
    now()
)
ON CONFLICT DO NOTHING;

SELECT count(*) AS visible_articles FROM cti.dashboard_articles;
SELECT count(*) AS visible_reports FROM cti.dashboard_reports;
SELECT * FROM cti.dashboard_ai_usage;
