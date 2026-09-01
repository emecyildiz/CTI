\set ON_ERROR_STOP on

CREATE OR REPLACE VIEW cti.dashboard_ai_usage
WITH (security_barrier = true)
AS
WITH boundaries AS (
    SELECT
        date_trunc('day', now() AT TIME ZONE 'UTC') AT TIME ZONE 'UTC' AS utc_day_start,
        date_trunc('month', now() AT TIME ZONE 'UTC') AT TIME ZONE 'UTC' AS utc_month_start
)
SELECT
    count(usage.id) FILTER (WHERE usage.requested_at >= boundaries.utc_day_start) AS today_requests,
    COALESCE(sum(usage.total_tokens) FILTER (
        WHERE usage.requested_at >= boundaries.utc_day_start
    ), 0)::bigint AS today_tokens,
    count(usage.id) AS month_requests,
    COALESCE(sum(usage.prompt_tokens), 0)::bigint AS month_prompt_tokens,
    COALESCE(sum(usage.output_tokens), 0)::bigint AS month_output_tokens,
    COALESCE(sum(usage.total_tokens), 0)::bigint AS month_total_tokens,
    count(usage.id) FILTER (WHERE usage.purpose = 'article_analysis') AS month_article_requests,
    count(usage.id) FILTER (WHERE usage.purpose = 'weekly_report') AS month_report_requests,
    count(usage.id) FILTER (WHERE usage.request_status <> 'success') AS month_failed_requests,
    max(usage.requested_at) AS last_requested_at
FROM boundaries
LEFT JOIN cti.ai_usage AS usage
    ON usage.requested_at >= boundaries.utc_month_start;

REVOKE ALL ON cti.dashboard_ai_usage FROM PUBLIC;
GRANT SELECT ON cti.dashboard_ai_usage TO cti_dashboard;

INSERT INTO cti.schema_versions (version)
VALUES (14)
ON CONFLICT (version) DO NOTHING;
