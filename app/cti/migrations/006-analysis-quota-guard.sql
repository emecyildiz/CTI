\set ON_ERROR_STOP on

DROP FUNCTION cti.claim_analysis_jobs(integer, integer);

CREATE OR REPLACE FUNCTION cti.claim_analysis_jobs(
    batch_size_value integer DEFAULT 1,
    daily_limit_value integer DEFAULT 20,
    monthly_limit_value integer DEFAULT 400
)
RETURNS TABLE(
    article_id bigint,
    canonical_url text,
    title text,
    source_name text,
    content_selector text,
    allowed_hosts text[],
    attempt smallint
)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, cti
AS $$
DECLARE
    reference_time timestamptz := clock_timestamp();
    utc_day_start timestamptz;
    utc_month_start timestamptz;
    used_daily_slots integer;
    used_monthly_slots integer;
    active_reservations integer;
    available_slots integer;
BEGIN
    IF batch_size_value NOT BETWEEN 1 AND 5 THEN
        RAISE EXCEPTION 'Analysis batch size must be between 1 and 5.'
            USING ERRCODE = '22023';
    END IF;

    IF daily_limit_value NOT BETWEEN 1 AND 100 THEN
        RAISE EXCEPTION 'Analysis daily limit must be between 1 and 100.'
            USING ERRCODE = '22023';
    END IF;

    IF monthly_limit_value NOT BETWEEN daily_limit_value AND 5000 THEN
        RAISE EXCEPTION 'Analysis monthly limit must be between the daily limit and 5000.'
            USING ERRCODE = '22023';
    END IF;

    PERFORM pg_advisory_xact_lock(hashtextextended('cti.claim_analysis_jobs', 0));

    utc_day_start := date_trunc('day', reference_time AT TIME ZONE 'UTC')
        AT TIME ZONE 'UTC';
    utc_month_start := date_trunc('month', reference_time AT TIME ZONE 'UTC')
        AT TIME ZONE 'UTC';

    UPDATE cti.analysis_jobs AS job
    SET status = CASE WHEN job.attempts >= 5 THEN 'failed' ELSE 'deferred' END,
        next_attempt_at = CASE
            WHEN job.attempts >= 5 THEN job.next_attempt_at
            ELSE reference_time + interval '15 minutes'
        END,
        locked_at = NULL,
        last_error_code = 'stale_lock_recovered',
        updated_at = reference_time
    WHERE job.status = 'processing'
      AND job.locked_at < reference_time - interval '30 minutes';

    SELECT count(*)
    INTO used_daily_slots
    FROM cti.ai_usage AS usage
    WHERE usage.purpose = 'article_analysis'
      AND usage.requested_at >= utc_day_start;

    SELECT count(*)
    INTO used_monthly_slots
    FROM cti.ai_usage AS usage
    WHERE usage.purpose = 'article_analysis'
      AND usage.requested_at >= utc_month_start;

    SELECT count(*)
    INTO active_reservations
    FROM cti.analysis_jobs AS job
    WHERE job.status = 'processing';

    IF active_reservations > 0 THEN
        RETURN;
    END IF;

    available_slots := LEAST(
        batch_size_value,
        GREATEST(daily_limit_value - used_daily_slots, 0),
        GREATEST(monthly_limit_value - used_monthly_slots, 0)
    );

    IF available_slots = 0 THEN
        RETURN;
    END IF;

    RETURN QUERY
    WITH candidates AS MATERIALIZED (
        SELECT job.id
        FROM cti.analysis_jobs AS job
        JOIN cti.articles AS article ON article.id = job.article_id
        WHERE job.status IN ('pending', 'deferred')
          AND job.next_attempt_at <= reference_time
          AND job.attempts < 5
          AND article.analyzed_at IS NULL
        ORDER BY job.priority DESC, article.published_at DESC, job.id
        FOR UPDATE OF job SKIP LOCKED
        LIMIT available_slots
    ), claimed AS (
        UPDATE cti.analysis_jobs AS job
        SET status = 'processing',
            attempts = job.attempts + 1,
            locked_at = reference_time,
            last_error_code = NULL,
            updated_at = reference_time
        FROM candidates
        WHERE job.id = candidates.id
        RETURNING job.article_id, job.attempts
    )
    SELECT
        article.id,
        article.canonical_url,
        article.title,
        selected_source.name,
        selected_source.content_selector,
        selected_source.allowed_hosts,
        claimed.attempts
    FROM claimed
    JOIN cti.articles AS article ON article.id = claimed.article_id
    JOIN LATERAL (
        SELECT source.name, source.content_selector, source.allowed_hosts
        FROM cti.article_occurrences AS occurrence
        JOIN cti.sources AS source ON source.id = occurrence.source_id
        WHERE occurrence.article_id = article.id
          AND source.enabled = true
        ORDER BY source.trust_score DESC, occurrence.discovered_at, occurrence.id
        LIMIT 1
    ) AS selected_source ON true
    ORDER BY article.published_at DESC, article.id;
END;
$$;

REVOKE ALL ON FUNCTION cti.claim_analysis_jobs(integer, integer, integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION cti.claim_analysis_jobs(integer, integer, integer) TO cti_n8n;

INSERT INTO cti.schema_versions (version)
VALUES (6)
ON CONFLICT (version) DO NOTHING;
