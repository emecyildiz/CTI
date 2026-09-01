\set ON_ERROR_STOP on

CREATE OR REPLACE FUNCTION cti.enqueue_article_analysis()
RETURNS trigger
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, cti
AS $$
BEGIN
    INSERT INTO cti.analysis_jobs (article_id)
    VALUES (NEW.id)
    ON CONFLICT (article_id) DO NOTHING;
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_enqueue_article_analysis ON cti.articles;
CREATE TRIGGER trg_enqueue_article_analysis
AFTER INSERT ON cti.articles
FOR EACH ROW
EXECUTE FUNCTION cti.enqueue_article_analysis();

CREATE OR REPLACE FUNCTION cti.claim_analysis_jobs(
    batch_size_value integer DEFAULT 3,
    daily_limit_value integer DEFAULT 20
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
    used_slots integer;
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

    utc_day_start := date_trunc('day', reference_time AT TIME ZONE 'UTC')
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
    INTO used_slots
    FROM cti.ai_usage AS usage
    WHERE usage.purpose = 'article_analysis'
      AND usage.requested_at >= utc_day_start;

    SELECT count(*)
    INTO active_reservations
    FROM cti.analysis_jobs AS job
    WHERE job.status = 'processing';

    available_slots := LEAST(
        batch_size_value,
        GREATEST(daily_limit_value - used_slots - active_reservations, 0)
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

CREATE OR REPLACE FUNCTION cti.complete_article_analysis(
    article_id_value bigint,
    category_value text,
    severity_value text,
    summary_tr_value text,
    cleaned_content_value text,
    confidence_value numeric,
    model_value text,
    prompt_tokens_value integer DEFAULT 0,
    output_tokens_value integer DEFAULT 0
)
RETURNS void
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, cti
AS $$
DECLARE
    reference_time timestamptz := clock_timestamp();
BEGIN
    IF model_value IS NULL OR char_length(trim(model_value)) NOT BETWEEN 1 AND 200 THEN
        RAISE EXCEPTION 'AI model name is invalid.' USING ERRCODE = '22023';
    END IF;

    IF prompt_tokens_value < 0 OR output_tokens_value < 0 THEN
        RAISE EXCEPTION 'Token counts cannot be negative.' USING ERRCODE = '22023';
    END IF;

    PERFORM 1
    FROM cti.analysis_jobs AS job
    WHERE job.article_id = article_id_value
      AND job.status = 'processing'
    FOR UPDATE;

    IF NOT FOUND THEN
        RAISE EXCEPTION 'The article does not have a claimed analysis job.'
            USING ERRCODE = '55000';
    END IF;

    UPDATE cti.articles
    SET category = category_value,
        severity = severity_value,
        summary_tr = summary_tr_value,
        cleaned_content = cleaned_content_value,
        analysis_confidence = confidence_value,
        analyzed_at = reference_time,
        updated_at = reference_time
    WHERE id = article_id_value;

    UPDATE cti.analysis_jobs
    SET status = 'completed',
        locked_at = NULL,
        last_error_code = NULL,
        updated_at = reference_time
    WHERE article_id = article_id_value;

    INSERT INTO cti.ai_usage (
        article_id,
        purpose,
        model,
        request_status,
        prompt_tokens,
        output_tokens,
        total_tokens,
        requested_at
    )
    VALUES (
        article_id_value,
        'article_analysis',
        trim(model_value),
        'success',
        prompt_tokens_value,
        output_tokens_value,
        prompt_tokens_value + output_tokens_value,
        reference_time
    );
END;
$$;

CREATE OR REPLACE FUNCTION cti.defer_article_analysis(
    article_id_value bigint,
    error_code_value text,
    ai_was_called_value boolean DEFAULT false,
    rate_limited_value boolean DEFAULT false,
    model_value text DEFAULT NULL
)
RETURNS text
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, cti
AS $$
DECLARE
    reference_time timestamptz := clock_timestamp();
    attempt_count smallint;
    next_status text;
    retry_delay interval;
BEGIN
    IF error_code_value IS NULL OR char_length(trim(error_code_value)) NOT BETWEEN 1 AND 100 THEN
        RAISE EXCEPTION 'Analysis error code is invalid.' USING ERRCODE = '22023';
    END IF;

    SELECT job.attempts
    INTO attempt_count
    FROM cti.analysis_jobs AS job
    WHERE job.article_id = article_id_value
      AND job.status = 'processing'
    FOR UPDATE;

    IF NOT FOUND THEN
        RAISE EXCEPTION 'The article does not have a claimed analysis job.'
            USING ERRCODE = '55000';
    END IF;

    next_status := CASE WHEN attempt_count >= 5 THEN 'failed' ELSE 'deferred' END;
    retry_delay := CASE
        WHEN rate_limited_value THEN interval '2 hours'
        WHEN attempt_count >= 4 THEN interval '4 hours'
        WHEN attempt_count = 3 THEN interval '2 hours'
        WHEN attempt_count = 2 THEN interval '30 minutes'
        ELSE interval '15 minutes'
    END;

    UPDATE cti.analysis_jobs
    SET status = next_status,
        next_attempt_at = CASE
            WHEN next_status = 'failed' THEN next_attempt_at
            ELSE reference_time + retry_delay
        END,
        locked_at = NULL,
        last_error_code = trim(error_code_value),
        updated_at = reference_time
    WHERE article_id = article_id_value;

    IF ai_was_called_value THEN
        IF model_value IS NULL OR char_length(trim(model_value)) NOT BETWEEN 1 AND 200 THEN
            RAISE EXCEPTION 'AI model name is required for a recorded request.'
                USING ERRCODE = '22023';
        END IF;

        INSERT INTO cti.ai_usage (
            article_id,
            purpose,
            model,
            request_status,
            requested_at
        )
        VALUES (
            article_id_value,
            'article_analysis',
            trim(model_value),
            CASE WHEN rate_limited_value THEN 'rate_limited' ELSE 'failed' END,
            reference_time
        );
    END IF;

    RETURN next_status;
END;
$$;

INSERT INTO cti.analysis_jobs (article_id)
SELECT article.id
FROM cti.articles AS article
WHERE article.analyzed_at IS NULL
ON CONFLICT (article_id) DO NOTHING;

REVOKE ALL ON FUNCTION cti.enqueue_article_analysis() FROM PUBLIC;
REVOKE ALL ON FUNCTION cti.claim_analysis_jobs(integer, integer) FROM PUBLIC;
REVOKE ALL ON FUNCTION cti.complete_article_analysis(
    bigint, text, text, text, text, numeric, text, integer, integer
) FROM PUBLIC;
REVOKE ALL ON FUNCTION cti.defer_article_analysis(
    bigint, text, boolean, boolean, text
) FROM PUBLIC;

GRANT EXECUTE ON FUNCTION cti.claim_analysis_jobs(integer, integer) TO cti_n8n;
GRANT EXECUTE ON FUNCTION cti.complete_article_analysis(
    bigint, text, text, text, text, numeric, text, integer, integer
) TO cti_n8n;
GRANT EXECUTE ON FUNCTION cti.defer_article_analysis(
    bigint, text, boolean, boolean, text
) TO cti_n8n;

INSERT INTO cti.schema_versions (version)
VALUES (5)
ON CONFLICT (version) DO NOTHING;
