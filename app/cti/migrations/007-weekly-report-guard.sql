\set ON_ERROR_STOP on

ALTER TABLE cti.reports
    ADD COLUMN IF NOT EXISTS attempts smallint NOT NULL DEFAULT 0
        CHECK (attempts BETWEEN 0 AND 3),
    ADD COLUMN IF NOT EXISTS locked_at timestamptz,
    ADD COLUMN IF NOT EXISTS last_error_code text
        CHECK (last_error_code IS NULL OR char_length(last_error_code) <= 100);

CREATE UNIQUE INDEX IF NOT EXISTS ux_reports_weekly_window
    ON cti.reports (window_start, window_end)
    WHERE report_type = 'weekly' AND category IS NULL;

CREATE OR REPLACE FUNCTION cti.claim_weekly_report(
    window_start_value timestamptz,
    window_end_value timestamptz,
    report_daily_limit_value integer DEFAULT 1,
    report_monthly_limit_value integer DEFAULT 8,
    provider_daily_limit_value integer DEFAULT 20,
    provider_monthly_limit_value integer DEFAULT 400,
    max_articles_value integer DEFAULT 40
)
RETURNS TABLE(
    report_id bigint,
    report_window_start timestamptz,
    report_window_end timestamptz,
    article_payload jsonb
)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, cti
AS $$
DECLARE
    reference_time timestamptz := clock_timestamp();
    utc_day_start timestamptz;
    utc_month_start timestamptz;
    total_daily_usage integer;
    total_monthly_usage integer;
    weekly_daily_usage integer;
    weekly_monthly_usage integer;
    active_analysis_reservations integer;
    selected_report cti.reports%ROWTYPE;
    selected_payload jsonb;
BEGIN
    IF window_start_value IS NULL
       OR window_end_value IS NULL
       OR window_end_value <= window_start_value
       OR window_end_value > reference_time + interval '15 minutes'
       OR window_end_value - window_start_value NOT BETWEEN interval '6 days' AND interval '8 days' THEN
        RAISE EXCEPTION 'Weekly report window must cover approximately seven completed days.'
            USING ERRCODE = '22023';
    END IF;

    IF report_daily_limit_value NOT BETWEEN 1 AND 5
       OR report_monthly_limit_value NOT BETWEEN report_daily_limit_value AND 31
       OR provider_daily_limit_value NOT BETWEEN report_daily_limit_value AND 100
       OR provider_monthly_limit_value NOT BETWEEN provider_daily_limit_value AND 5000
       OR max_articles_value NOT BETWEEN 1 AND 80 THEN
        RAISE EXCEPTION 'Weekly report quota or article limit is invalid.'
            USING ERRCODE = '22023';
    END IF;

    PERFORM pg_advisory_xact_lock(hashtextextended('cti.claim_weekly_report', 0));

    utc_day_start := date_trunc('day', reference_time AT TIME ZONE 'UTC')
        AT TIME ZONE 'UTC';
    utc_month_start := date_trunc('month', reference_time AT TIME ZONE 'UTC')
        AT TIME ZONE 'UTC';

    UPDATE cti.reports AS report
    SET status = 'failed', locked_at = NULL, last_error_code = 'stale_lock_recovered'
    WHERE report.report_type = 'weekly'
      AND report.status = 'draft'
      AND report.locked_at < reference_time - interval '30 minutes';

    SELECT count(*) INTO total_daily_usage
    FROM cti.ai_usage AS usage WHERE usage.requested_at >= utc_day_start;
    SELECT count(*) INTO total_monthly_usage
    FROM cti.ai_usage AS usage WHERE usage.requested_at >= utc_month_start;
    SELECT count(*) INTO weekly_daily_usage
    FROM cti.ai_usage AS usage
    WHERE usage.purpose = 'weekly_report' AND usage.requested_at >= utc_day_start;
    SELECT count(*) INTO weekly_monthly_usage
    FROM cti.ai_usage AS usage
    WHERE usage.purpose = 'weekly_report' AND usage.requested_at >= utc_month_start;
    SELECT count(*) INTO active_analysis_reservations
    FROM cti.analysis_jobs AS job WHERE job.status = 'processing';

    IF weekly_daily_usage >= report_daily_limit_value
       OR weekly_monthly_usage >= report_monthly_limit_value
       OR total_daily_usage + active_analysis_reservations >= provider_daily_limit_value
       OR total_monthly_usage + active_analysis_reservations >= provider_monthly_limit_value THEN
        RETURN;
    END IF;

    SELECT report.* INTO selected_report
    FROM cti.reports AS report
    WHERE report.report_type = 'weekly'
      AND report.category IS NULL
      AND report.window_start = window_start_value
      AND report.window_end = window_end_value
    FOR UPDATE;

    IF FOUND THEN
        IF selected_report.status <> 'failed' OR selected_report.attempts >= 3 THEN
            RETURN;
        END IF;
        UPDATE cti.reports AS report
        SET status = 'draft', attempts = report.attempts + 1,
            locked_at = reference_time, last_error_code = NULL,
            generated_at = reference_time
        WHERE report.id = selected_report.id
        RETURNING report.* INTO selected_report;
    ELSE
        INSERT INTO cti.reports (
            report_type, window_start, window_end, status, title, content,
            generated_at, expires_at, attempts, locked_at
        ) VALUES (
            'weekly', window_start_value, window_end_value, 'draft',
            'Pending weekly CTI report', 'Pending AI generation.',
            reference_time, reference_time + interval '8 weeks', 1, reference_time
        ) RETURNING * INTO selected_report;
    END IF;

    SELECT jsonb_agg(to_jsonb(candidate) ORDER BY candidate.severity_rank, candidate.published_at DESC)
    INTO selected_payload
    FROM (
        SELECT article.id AS article_id, article.title, article.category,
            article.severity, left(article.summary_tr, 800) AS summary_tr,
            article.analysis_confidence, article.published_at,
            article.canonical_url, selected_source.name AS source_name,
            CASE article.severity WHEN 'critical' THEN 1 WHEN 'high' THEN 2
                WHEN 'medium' THEN 3 WHEN 'low' THEN 4 ELSE 5 END AS severity_rank
        FROM cti.articles AS article
        JOIN LATERAL (
            SELECT source.name
            FROM cti.article_occurrences AS occurrence
            JOIN cti.sources AS source ON source.id = occurrence.source_id
            WHERE occurrence.article_id = article.id
            ORDER BY source.trust_score DESC, occurrence.discovered_at, occurrence.id
            LIMIT 1
        ) AS selected_source ON true
        WHERE article.analyzed_at IS NOT NULL
          AND article.summary_tr IS NOT NULL
          AND article.published_at >= window_start_value
          AND article.published_at < window_end_value
        ORDER BY severity_rank, article.published_at DESC, article.id
        LIMIT max_articles_value
    ) AS candidate;

    IF selected_payload IS NULL THEN
        DELETE FROM cti.reports WHERE id = selected_report.id;
        RETURN;
    END IF;

    report_id := selected_report.id;
    report_window_start := selected_report.window_start;
    report_window_end := selected_report.window_end;
    article_payload := selected_payload;
    RETURN NEXT;
END;
$$;

CREATE OR REPLACE FUNCTION cti.complete_weekly_report(
    report_id_value bigint,
    title_value text,
    content_value text,
    article_ids_value bigint[],
    model_value text,
    prompt_tokens_value integer DEFAULT 0,
    output_tokens_value integer DEFAULT 0,
    total_tokens_value integer DEFAULT 0
)
RETURNS bigint
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, cti
AS $$
DECLARE
    reference_time timestamptz := clock_timestamp();
    selected_report cti.reports%ROWTYPE;
    valid_article_count integer;
BEGIN
    IF title_value IS NULL OR char_length(trim(title_value)) NOT BETWEEN 1 AND 500
       OR content_value IS NULL OR char_length(trim(content_value)) NOT BETWEEN 1 AND 100000
       OR article_ids_value IS NULL OR cardinality(article_ids_value) NOT BETWEEN 1 AND 80
       OR model_value IS NULL OR char_length(trim(model_value)) NOT BETWEEN 1 AND 200
       OR prompt_tokens_value < 0 OR output_tokens_value < 0
       OR total_tokens_value < prompt_tokens_value + output_tokens_value THEN
        RAISE EXCEPTION 'Completed weekly report data is invalid.' USING ERRCODE = '22023';
    END IF;

    SELECT report.* INTO selected_report
    FROM cti.reports AS report
    WHERE report.id = report_id_value AND report.report_type = 'weekly'
      AND report.status = 'draft' AND report.locked_at IS NOT NULL
    FOR UPDATE;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'Weekly report reservation is not active.' USING ERRCODE = '55000';
    END IF;

    SELECT count(DISTINCT article.id) INTO valid_article_count
    FROM cti.articles AS article
    WHERE article.id = ANY(article_ids_value)
      AND article.analyzed_at IS NOT NULL
      AND article.published_at >= selected_report.window_start
      AND article.published_at < selected_report.window_end;
    IF valid_article_count <> cardinality(article_ids_value) THEN
        RAISE EXCEPTION 'Weekly report article selection is invalid.' USING ERRCODE = '22023';
    END IF;

    UPDATE cti.reports AS report
    SET status = 'ready', title = trim(title_value), content = trim(content_value),
        locked_at = NULL, last_error_code = NULL, generated_at = reference_time
    WHERE report.id = selected_report.id;

    INSERT INTO cti.report_articles (
        report_id, article_id, title_snapshot, url_snapshot, source_snapshot
    )
    SELECT selected_report.id, article.id, article.title, article.canonical_url,
        selected_source.name
    FROM cti.articles AS article
    JOIN LATERAL (
        SELECT source.name
        FROM cti.article_occurrences AS occurrence
        JOIN cti.sources AS source ON source.id = occurrence.source_id
        WHERE occurrence.article_id = article.id
        ORDER BY source.trust_score DESC, occurrence.discovered_at, occurrence.id
        LIMIT 1
    ) AS selected_source ON true
    WHERE article.id = ANY(article_ids_value)
    ON CONFLICT (report_id, url_snapshot) DO NOTHING;

    UPDATE cti.articles AS article
    SET reported_at = reference_time, updated_at = reference_time
    WHERE article.id = ANY(article_ids_value);

    INSERT INTO cti.ai_usage (
        purpose, model, request_status, prompt_tokens,
        output_tokens, total_tokens, requested_at
    ) VALUES (
        'weekly_report', trim(model_value), 'success', prompt_tokens_value,
        output_tokens_value, total_tokens_value, reference_time
    );
    RETURN selected_report.id;
END;
$$;

CREATE OR REPLACE FUNCTION cti.fail_weekly_report(
    report_id_value bigint,
    model_value text,
    rate_limited_value boolean,
    error_code_value text
)
RETURNS void
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, cti
AS $$
DECLARE
    reference_time timestamptz := clock_timestamp();
    updated_reports integer;
BEGIN
    IF model_value IS NULL OR char_length(trim(model_value)) NOT BETWEEN 1 AND 200
       OR error_code_value IS NULL OR char_length(trim(error_code_value)) NOT BETWEEN 1 AND 100 THEN
        RAISE EXCEPTION 'Weekly report failure data is invalid.' USING ERRCODE = '22023';
    END IF;

    UPDATE cti.reports AS report
    SET status = 'failed', locked_at = NULL,
        last_error_code = trim(error_code_value), generated_at = reference_time
    WHERE report.id = report_id_value AND report.report_type = 'weekly'
      AND report.status = 'draft';
    GET DIAGNOSTICS updated_reports = ROW_COUNT;
    IF updated_reports <> 1 THEN
        RAISE EXCEPTION 'Weekly report reservation is not active.' USING ERRCODE = '55000';
    END IF;

    INSERT INTO cti.ai_usage (purpose, model, request_status, requested_at)
    VALUES ('weekly_report', trim(model_value),
        CASE WHEN rate_limited_value THEN 'rate_limited' ELSE 'failed' END,
        reference_time);
END;
$$;

REVOKE ALL ON FUNCTION cti.claim_weekly_report(
    timestamptz, timestamptz, integer, integer, integer, integer, integer
) FROM PUBLIC;
REVOKE ALL ON FUNCTION cti.complete_weekly_report(
    bigint, text, text, bigint[], text, integer, integer, integer
) FROM PUBLIC;
REVOKE ALL ON FUNCTION cti.fail_weekly_report(bigint, text, boolean, text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION cti.claim_weekly_report(
    timestamptz, timestamptz, integer, integer, integer, integer, integer
) TO cti_n8n;
GRANT EXECUTE ON FUNCTION cti.complete_weekly_report(
    bigint, text, text, bigint[], text, integer, integer, integer
) TO cti_n8n;
GRANT EXECUTE ON FUNCTION cti.fail_weekly_report(bigint, text, boolean, text)
    TO cti_n8n;

INSERT INTO cti.schema_versions (version)
VALUES (7)
ON CONFLICT (version) DO NOTHING;
