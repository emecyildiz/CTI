\set ON_ERROR_STOP on

SELECT format(
    'CREATE ROLE cti_dashboard LOGIN NOINHERIT PASSWORD %L',
    :'cti_dashboard_password'
)
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'cti_dashboard')
\gexec

SELECT format('ALTER ROLE cti_dashboard PASSWORD %L', :'cti_dashboard_password')
\gexec

ALTER ROLE cti_dashboard SET default_transaction_read_only = on;
ALTER ROLE cti_dashboard SET statement_timeout = '10s';
ALTER ROLE cti_dashboard SET idle_in_transaction_session_timeout = '10s';

CREATE OR REPLACE VIEW cti.dashboard_articles
WITH (security_barrier = true)
AS
SELECT
    article.id,
    article.title,
    article.category,
    article.severity,
    article.summary_tr,
    article.canonical_url,
    article.published_at,
    article.analyzed_at,
    ARRAY(
        SELECT DISTINCT source.name
        FROM cti.article_occurrences AS occurrence
        JOIN cti.sources AS source ON source.id = occurrence.source_id
        WHERE occurrence.article_id = article.id
        ORDER BY source.name
    ) AS source_names
FROM cti.articles AS article
WHERE article.analyzed_at IS NOT NULL
  AND article.summary_tr IS NOT NULL
  AND article.category IS NOT NULL
  AND article.severity IS NOT NULL
  AND article.published_at >= now() - interval '30 days';

CREATE OR REPLACE VIEW cti.dashboard_reports
WITH (security_barrier = true)
AS
SELECT
    report.id,
    report.report_type,
    report.window_start,
    report.window_end,
    report.category,
    report.status,
    report.title,
    report.content,
    report.generated_at,
    report.sent_at,
    report.expires_at
FROM cti.reports AS report
WHERE report.status IN ('ready', 'sent')
  AND report.expires_at > now();

REVOKE ALL ON ALL TABLES IN SCHEMA cti FROM cti_dashboard;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA cti FROM cti_dashboard;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA cti FROM cti_dashboard;
REVOKE TEMPORARY ON DATABASE cti FROM cti_dashboard;

GRANT CONNECT ON DATABASE cti TO cti_dashboard;
GRANT USAGE ON SCHEMA cti TO cti_dashboard;
GRANT SELECT ON cti.dashboard_articles, cti.dashboard_reports TO cti_dashboard;

INSERT INTO cti.schema_versions (version)
VALUES (10)
ON CONFLICT (version) DO NOTHING;
