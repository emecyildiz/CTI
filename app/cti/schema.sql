\set ON_ERROR_STOP on

SELECT format('CREATE ROLE cti_n8n LOGIN PASSWORD %L', :'cti_app_password')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'cti_n8n')
\gexec

SELECT format('ALTER ROLE cti_n8n PASSWORD %L', :'cti_app_password')
\gexec

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

CREATE EXTENSION IF NOT EXISTS pg_trgm;
CREATE EXTENSION IF NOT EXISTS pgcrypto;
CREATE SCHEMA IF NOT EXISTS cti AUTHORIZATION CURRENT_USER;

CREATE TABLE IF NOT EXISTS cti.schema_versions (
    version integer PRIMARY KEY,
    applied_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS cti.sources (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    name text NOT NULL UNIQUE,
    feed_url text NOT NULL UNIQUE CHECK (feed_url ~ '^https://'),
    allowed_hosts text[] NOT NULL CHECK (cardinality(allowed_hosts) > 0),
    content_selector text,
    trust_score smallint NOT NULL DEFAULT 50 CHECK (trust_score BETWEEN 0 AND 100),
    enabled boolean NOT NULL DEFAULT true,
    last_checked_at timestamptz,
    last_success_at timestamptz,
    last_error_at timestamptz,
    last_error_code text CHECK (
        last_error_code IS NULL OR last_error_code IN (
            'feed_read_failed',
            'feed_item_invalid',
            'feed_item_store_failed'
        )
    ),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS cti.articles (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    canonical_url text NOT NULL CHECK (canonical_url ~ '^https://'),
    title text NOT NULL CHECK (char_length(title) BETWEEN 1 AND 500),
    normalized_title text NOT NULL CHECK (char_length(normalized_title) BETWEEN 1 AND 500),
    title_hash char(64) NOT NULL CHECK (title_hash ~ '^[0-9a-f]{64}$'),
    content_hash char(64) CHECK (content_hash ~ '^[0-9a-f]{64}$'),
    cleaned_content text CHECK (cleaned_content IS NULL OR char_length(cleaned_content) <= 20000),
    category text CHECK (category IN (
        'malware',
        'vulnerability',
        'data_breach',
        'threat_intelligence',
        'other'
    )),
    severity text CHECK (severity IN ('critical', 'high', 'medium', 'low', 'unknown')),
    summary_tr text CHECK (summary_tr IS NULL OR char_length(summary_tr) <= 4000),
    analysis_confidence numeric(4,3) CHECK (
        analysis_confidence IS NULL OR analysis_confidence BETWEEN 0 AND 1
    ),
    published_at timestamptz NOT NULL,
    fetched_at timestamptz NOT NULL DEFAULT now(),
    analyzed_at timestamptz,
    reported_at timestamptz,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (canonical_url)
);

CREATE INDEX IF NOT EXISTS ix_articles_published_at
    ON cti.articles (published_at DESC);
CREATE INDEX IF NOT EXISTS ix_articles_category_published_at
    ON cti.articles (category, published_at DESC);
CREATE INDEX IF NOT EXISTS ix_articles_normalized_title_trgm
    ON cti.articles USING gin (normalized_title gin_trgm_ops);
CREATE INDEX IF NOT EXISTS ix_articles_content_hash
    ON cti.articles (content_hash) WHERE content_hash IS NOT NULL;

CREATE INDEX IF NOT EXISTS ix_articles_simple_search
    ON cti.articles USING gin (
        to_tsvector('simple', title || ' ' || coalesce(summary_tr, ''))
    );

CREATE TABLE IF NOT EXISTS cti.article_occurrences (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    article_id bigint NOT NULL REFERENCES cti.articles(id) ON DELETE CASCADE,
    source_id bigint NOT NULL REFERENCES cti.sources(id) ON DELETE RESTRICT,
    original_url text NOT NULL CHECK (
        original_url ~ '^https://' AND char_length(original_url) <= 4000
    ),
    source_title text NOT NULL CHECK (char_length(source_title) BETWEEN 1 AND 500),
    feed_guid text CHECK (feed_guid IS NULL OR char_length(feed_guid) <= 1000),
    source_published_at timestamptz NOT NULL,
    discovered_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (source_id, original_url)
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_article_occurrences_source_guid
    ON cti.article_occurrences (source_id, feed_guid)
    WHERE feed_guid IS NOT NULL AND feed_guid <> '';

CREATE TABLE IF NOT EXISTS cti.fingerprints (
    kind text NOT NULL CHECK (kind IN ('url', 'content')),
    fingerprint char(64) NOT NULL CHECK (fingerprint ~ '^[0-9a-f]{64}$'),
    article_id bigint NOT NULL REFERENCES cti.articles(id) ON DELETE CASCADE,
    first_seen_at timestamptz NOT NULL DEFAULT now(),
    expires_at timestamptz NOT NULL DEFAULT (now() + interval '30 days'),
    PRIMARY KEY (kind, fingerprint)
);

CREATE INDEX IF NOT EXISTS ix_fingerprints_expires_at
    ON cti.fingerprints (expires_at);

CREATE TABLE IF NOT EXISTS cti.analysis_jobs (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    article_id bigint NOT NULL UNIQUE REFERENCES cti.articles(id) ON DELETE CASCADE,
    status text NOT NULL DEFAULT 'pending' CHECK (
        status IN ('pending', 'processing', 'completed', 'deferred', 'failed')
    ),
    priority smallint NOT NULL DEFAULT 50 CHECK (priority BETWEEN 0 AND 100),
    attempts smallint NOT NULL DEFAULT 0 CHECK (attempts BETWEEN 0 AND 20),
    next_attempt_at timestamptz NOT NULL DEFAULT now(),
    locked_at timestamptz,
    last_error_code text CHECK (last_error_code IS NULL OR char_length(last_error_code) <= 100),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_analysis_jobs_ready
    ON cti.analysis_jobs (priority DESC, next_attempt_at, id)
    WHERE status IN ('pending', 'deferred');

CREATE TABLE IF NOT EXISTS cti.article_rule_triage (
    article_id bigint PRIMARY KEY REFERENCES cti.articles(id) ON DELETE CASCADE,
    ruleset_version text NOT NULL CHECK (char_length(ruleset_version) BETWEEN 1 AND 50),
    cves text[] NOT NULL DEFAULT ARRAY[]::text[] CHECK (cardinality(cves) <= 50),
    cvss_max numeric(3,1) CHECK (cvss_max IS NULL OR cvss_max BETWEEN 0 AND 10),
    has_active_exploitation boolean NOT NULL DEFAULT false,
    has_patch boolean NOT NULL DEFAULT false,
    has_poc boolean NOT NULL DEFAULT false,
    detected_category text NOT NULL CHECK (detected_category IN (
        'malware',
        'vulnerability',
        'data_breach',
        'threat_intelligence',
        'other'
    )),
    detected_severity text NOT NULL CHECK (
        detected_severity IN ('critical', 'high', 'medium', 'low', 'unknown')
    ),
    rule_summary text NOT NULL CHECK (char_length(rule_summary) BETWEEN 20 AND 1200),
    rule_confidence numeric(4,3) NOT NULL CHECK (rule_confidence BETWEEN 0 AND 1),
    priority_score smallint NOT NULL CHECK (priority_score BETWEEN 0 AND 100),
    source_trust_score smallint NOT NULL CHECK (source_trust_score BETWEEN 0 AND 100),
    ai_recommendation text NOT NULL CHECK (
        ai_recommendation IN ('required', 'candidate_bypass')
    ),
    decision_reasons text[] NOT NULL CHECK (
        cardinality(decision_reasons) BETWEEN 1 AND 20
    ),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_article_rule_triage_recommendation
    ON cti.article_rule_triage (ai_recommendation, rule_confidence DESC, priority_score DESC);

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

CREATE TABLE IF NOT EXISTS cti.ai_usage (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    article_id bigint REFERENCES cti.articles(id) ON DELETE SET NULL,
    purpose text NOT NULL CHECK (purpose IN ('article_analysis', 'weekly_report', 'manual_summary')),
    model text NOT NULL CHECK (char_length(model) BETWEEN 1 AND 200),
    request_status text NOT NULL CHECK (request_status IN ('success', 'rate_limited', 'failed')),
    prompt_tokens integer NOT NULL DEFAULT 0 CHECK (prompt_tokens >= 0),
    output_tokens integer NOT NULL DEFAULT 0 CHECK (output_tokens >= 0),
    total_tokens integer NOT NULL DEFAULT 0 CHECK (
        total_tokens >= prompt_tokens + output_tokens
    ),
    requested_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_ai_usage_requested_at
    ON cti.ai_usage (requested_at DESC);

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
    WHERE usage.requested_at >= utc_day_start;

    SELECT count(*)
    INTO used_monthly_slots
    FROM cti.ai_usage AS usage
    WHERE usage.requested_at >= utc_month_start;

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

CREATE OR REPLACE FUNCTION cti.record_article_rule_triage(
    article_id_value bigint,
    triage_value jsonb
)
RETURNS void
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, cti
AS $$
DECLARE
    reference_time timestamptz := clock_timestamp();
    ruleset_version_value text;
    cves_value text[];
    cvss_max_value numeric;
    has_active_exploitation_value boolean;
    has_patch_value boolean;
    has_poc_value boolean;
    detected_category_value text;
    detected_severity_value text;
    rule_summary_value text;
    rule_confidence_value numeric;
    priority_score_value integer;
    source_trust_score_value integer;
    ai_recommendation_value text;
    decision_reasons_value text[];
BEGIN
    IF jsonb_typeof(triage_value) IS DISTINCT FROM 'object' THEN
        RAISE EXCEPTION 'Rule triage payload must be a JSON object.'
            USING ERRCODE = '22023';
    END IF;

    IF jsonb_typeof(triage_value -> 'cves') IS DISTINCT FROM 'array' OR
       jsonb_typeof(triage_value -> 'decision_reasons') IS DISTINCT FROM 'array' THEN
        RAISE EXCEPTION 'Rule triage arrays are invalid.' USING ERRCODE = '22023';
    END IF;

    ruleset_version_value := trim(triage_value ->> 'ruleset_version');
    cvss_max_value := NULLIF(triage_value ->> 'cvss_max', '')::numeric;
    has_active_exploitation_value := COALESCE(
        (triage_value ->> 'has_active_exploitation')::boolean,
        false
    );
    has_patch_value := COALESCE((triage_value ->> 'has_patch')::boolean, false);
    has_poc_value := COALESCE((triage_value ->> 'has_poc')::boolean, false);
    detected_category_value := trim(triage_value ->> 'detected_category');
    detected_severity_value := trim(triage_value ->> 'detected_severity');
    rule_summary_value := trim(triage_value ->> 'rule_summary');
    rule_confidence_value := (triage_value ->> 'rule_confidence')::numeric;
    priority_score_value := (triage_value ->> 'priority_score')::integer;
    source_trust_score_value := (triage_value ->> 'source_trust_score')::integer;
    ai_recommendation_value := trim(triage_value ->> 'ai_recommendation');

    SELECT COALESCE(
        array_agg(DISTINCT upper(trim(value)) ORDER BY upper(trim(value))),
        ARRAY[]::text[]
    )
    INTO cves_value
    FROM jsonb_array_elements_text(triage_value -> 'cves') AS item(value)
    WHERE trim(value) <> '';

    SELECT COALESCE(
        array_agg(DISTINCT lower(trim(value)) ORDER BY lower(trim(value))),
        ARRAY[]::text[]
    )
    INTO decision_reasons_value
    FROM jsonb_array_elements_text(triage_value -> 'decision_reasons') AS item(value)
    WHERE trim(value) <> '';

    IF ruleset_version_value IS NULL OR
       char_length(ruleset_version_value) NOT BETWEEN 1 AND 50 THEN
        RAISE EXCEPTION 'Rule triage version is invalid.' USING ERRCODE = '22023';
    END IF;

    IF cardinality(cves_value) > 50 OR EXISTS (
        SELECT 1
        FROM unnest(cves_value) AS cve(value)
        WHERE value !~ '^CVE-[0-9]{4}-[0-9]{4,}$'
    ) THEN
        RAISE EXCEPTION 'Rule triage CVE list is invalid.' USING ERRCODE = '22023';
    END IF;

    IF cvss_max_value IS NOT NULL AND cvss_max_value NOT BETWEEN 0 AND 10 THEN
        RAISE EXCEPTION 'Rule triage CVSS score is invalid.' USING ERRCODE = '22023';
    END IF;

    IF detected_category_value NOT IN (
        'malware', 'vulnerability', 'data_breach', 'threat_intelligence', 'other'
    ) THEN
        RAISE EXCEPTION 'Rule triage category is invalid.' USING ERRCODE = '22023';
    END IF;

    IF detected_severity_value NOT IN ('critical', 'high', 'medium', 'low', 'unknown') THEN
        RAISE EXCEPTION 'Rule triage severity is invalid.' USING ERRCODE = '22023';
    END IF;

    IF rule_summary_value IS NULL OR char_length(rule_summary_value) NOT BETWEEN 20 AND 1200 THEN
        RAISE EXCEPTION 'Rule triage summary is invalid.' USING ERRCODE = '22023';
    END IF;

    IF rule_confidence_value NOT BETWEEN 0 AND 1 OR
       priority_score_value NOT BETWEEN 0 AND 100 OR
       source_trust_score_value NOT BETWEEN 0 AND 100 THEN
        RAISE EXCEPTION 'Rule triage scores are invalid.' USING ERRCODE = '22023';
    END IF;

    IF ai_recommendation_value NOT IN ('required', 'candidate_bypass') THEN
        RAISE EXCEPTION 'Rule triage AI recommendation is invalid.' USING ERRCODE = '22023';
    END IF;

    IF cardinality(decision_reasons_value) NOT BETWEEN 1 AND 20 OR EXISTS (
        SELECT 1
        FROM unnest(decision_reasons_value) AS reason(value)
        WHERE char_length(value) NOT BETWEEN 1 AND 100
           OR value !~ '^[a-z0-9_]+$'
    ) THEN
        RAISE EXCEPTION 'Rule triage decision reasons are invalid.' USING ERRCODE = '22023';
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

    INSERT INTO cti.article_rule_triage (
        article_id,
        ruleset_version,
        cves,
        cvss_max,
        has_active_exploitation,
        has_patch,
        has_poc,
        detected_category,
        detected_severity,
        rule_summary,
        rule_confidence,
        priority_score,
        source_trust_score,
        ai_recommendation,
        decision_reasons,
        created_at,
        updated_at
    )
    VALUES (
        article_id_value,
        ruleset_version_value,
        cves_value,
        cvss_max_value,
        has_active_exploitation_value,
        has_patch_value,
        has_poc_value,
        detected_category_value,
        detected_severity_value,
        rule_summary_value,
        rule_confidence_value,
        priority_score_value,
        source_trust_score_value,
        ai_recommendation_value,
        decision_reasons_value,
        reference_time,
        reference_time
    )
    ON CONFLICT (article_id) DO UPDATE
    SET ruleset_version = EXCLUDED.ruleset_version,
        cves = EXCLUDED.cves,
        cvss_max = EXCLUDED.cvss_max,
        has_active_exploitation = EXCLUDED.has_active_exploitation,
        has_patch = EXCLUDED.has_patch,
        has_poc = EXCLUDED.has_poc,
        detected_category = EXCLUDED.detected_category,
        detected_severity = EXCLUDED.detected_severity,
        rule_summary = EXCLUDED.rule_summary,
        rule_confidence = EXCLUDED.rule_confidence,
        priority_score = EXCLUDED.priority_score,
        source_trust_score = EXCLUDED.source_trust_score,
        ai_recommendation = EXCLUDED.ai_recommendation,
        decision_reasons = EXCLUDED.decision_reasons,
        updated_at = reference_time;
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

CREATE TABLE IF NOT EXISTS cti.reports (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    report_type text NOT NULL CHECK (report_type IN ('daily', 'weekly', 'on_demand')),
    window_start timestamptz NOT NULL,
    window_end timestamptz NOT NULL CHECK (window_end > window_start),
    category text,
    status text NOT NULL DEFAULT 'draft' CHECK (status IN ('draft', 'ready', 'sent', 'failed')),
    title text NOT NULL CHECK (char_length(title) BETWEEN 1 AND 500),
    content text NOT NULL CHECK (char_length(content) BETWEEN 1 AND 100000),
    generated_at timestamptz NOT NULL DEFAULT now(),
    sent_at timestamptz,
    expires_at timestamptz NOT NULL DEFAULT (now() + interval '8 weeks'),
    attempts smallint NOT NULL DEFAULT 0 CHECK (attempts BETWEEN 0 AND 3),
    locked_at timestamptz,
    last_error_code text CHECK (
        last_error_code IS NULL OR char_length(last_error_code) <= 100
    ),
    CHECK ((status = 'sent' AND sent_at IS NOT NULL) OR status <> 'sent')
);

CREATE INDEX IF NOT EXISTS ix_reports_expires_at
    ON cti.reports (expires_at);

CREATE UNIQUE INDEX IF NOT EXISTS ux_reports_weekly_window
    ON cti.reports (window_start, window_end)
    WHERE report_type = 'weekly' AND category IS NULL;

CREATE TABLE IF NOT EXISTS cti.report_articles (
    report_id bigint NOT NULL REFERENCES cti.reports(id) ON DELETE CASCADE,
    article_id bigint REFERENCES cti.articles(id) ON DELETE SET NULL,
    title_snapshot text NOT NULL CHECK (char_length(title_snapshot) BETWEEN 1 AND 500),
    url_snapshot text NOT NULL CHECK (url_snapshot ~ '^https://'),
    source_snapshot text NOT NULL CHECK (char_length(source_snapshot) BETWEEN 1 AND 200),
    PRIMARY KEY (report_id, url_snapshot)
);

CREATE TABLE IF NOT EXISTS cti.delivery_log (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    report_id bigint REFERENCES cti.reports(id) ON DELETE SET NULL,
    channel text NOT NULL CHECK (channel IN ('telegram', 'panel')),
    status text NOT NULL CHECK (status IN ('queued', 'sent', 'failed')),
    external_message_id text,
    error_code text CHECK (error_code IS NULL OR char_length(error_code) <= 100),
    attempts smallint NOT NULL DEFAULT 0 CHECK (attempts BETWEEN 0 AND 3),
    locked_at timestamptz,
    attempted_at timestamptz NOT NULL DEFAULT now()
);

ALTER TABLE cti.delivery_log
    ADD COLUMN IF NOT EXISTS attempts smallint NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS locked_at timestamptz;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conrelid = 'cti.delivery_log'::regclass
          AND conname = 'delivery_log_attempts_check'
    ) THEN
        ALTER TABLE cti.delivery_log
            ADD CONSTRAINT delivery_log_attempts_check
            CHECK (attempts BETWEEN 0 AND 3);
    END IF;
END;
$$;

CREATE UNIQUE INDEX IF NOT EXISTS ux_delivery_log_report_channel
    ON cti.delivery_log (report_id, channel)
    WHERE report_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS ix_delivery_log_attempted_at
    ON cti.delivery_log (attempted_at DESC);

CREATE OR REPLACE FUNCTION cti.claim_weekly_telegram_delivery()
RETURNS TABLE (
    delivery_id bigint,
    report_id bigint,
    report_title text,
    report_content text
)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = cti, pg_temp
AS $$
DECLARE
    reference_time timestamptz := now();
    selected_report_id bigint;
BEGIN
    PERFORM pg_advisory_xact_lock(hashtextextended('cti.claim_weekly_telegram_delivery', 0));

    UPDATE cti.delivery_log AS delivery
    SET status = 'failed',
        error_code = 'ambiguous:stale_lock',
        locked_at = NULL,
        attempted_at = reference_time
    WHERE delivery.channel = 'telegram'
      AND delivery.status = 'queued'
      AND (delivery.locked_at IS NULL OR delivery.locked_at < reference_time - interval '15 minutes');

    SELECT report.id
    INTO selected_report_id
    FROM cti.reports AS report
    LEFT JOIN cti.delivery_log AS delivery
      ON delivery.report_id = report.id
     AND delivery.channel = 'telegram'
    WHERE report.report_type = 'weekly'
      AND report.status = 'ready'
      AND (
          delivery.id IS NULL
          OR (
              delivery.status = 'failed'
              AND delivery.attempts < 3
              AND delivery.error_code LIKE 'retry_safe:%'
          )
      )
    ORDER BY report.window_end, report.id
    LIMIT 1
    FOR UPDATE OF report SKIP LOCKED;

    IF selected_report_id IS NULL THEN
        RETURN;
    END IF;

    UPDATE cti.delivery_log AS delivery
    SET
        status = 'queued',
        attempts = delivery.attempts + 1,
        locked_at = reference_time,
        attempted_at = reference_time,
        error_code = NULL,
        external_message_id = NULL
    WHERE delivery.report_id = selected_report_id
      AND delivery.channel = 'telegram';

    IF NOT FOUND THEN
        INSERT INTO cti.delivery_log (
            report_id, channel, status, attempts, locked_at, attempted_at
        )
        VALUES (
            selected_report_id, 'telegram', 'queued', 1, reference_time, reference_time
        );
    END IF;

    RETURN QUERY
    SELECT delivery.id, report.id, report.title, report.content
    FROM cti.delivery_log AS delivery
    JOIN cti.reports AS report ON report.id = delivery.report_id
    WHERE report.id = selected_report_id
      AND delivery.channel = 'telegram'
      AND delivery.status = 'queued';
END;
$$;

CREATE OR REPLACE FUNCTION cti.complete_weekly_telegram_delivery(
    delivery_id_value bigint,
    external_message_ids_value text
)
RETURNS void
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = cti, pg_temp
AS $$
DECLARE
    selected_report_id bigint;
    reference_time timestamptz := now();
    updated_reports integer;
BEGIN
    IF external_message_ids_value IS NULL
       OR char_length(trim(external_message_ids_value)) NOT BETWEEN 1 AND 1000 THEN
        RAISE EXCEPTION 'Telegram delivery receipt is invalid.' USING ERRCODE = '22023';
    END IF;

    UPDATE cti.delivery_log AS delivery
    SET status = 'sent',
        external_message_id = trim(external_message_ids_value),
        error_code = NULL,
        locked_at = NULL,
        attempted_at = reference_time
    WHERE delivery.id = delivery_id_value
      AND delivery.channel = 'telegram'
      AND delivery.status = 'queued'
    RETURNING delivery.report_id INTO selected_report_id;

    IF selected_report_id IS NULL THEN
        RAISE EXCEPTION 'Telegram delivery reservation is not active.' USING ERRCODE = '55000';
    END IF;

    UPDATE cti.reports AS report
    SET status = 'sent', sent_at = reference_time
    WHERE report.id = selected_report_id
      AND report.report_type = 'weekly'
      AND report.status = 'ready';
    GET DIAGNOSTICS updated_reports = ROW_COUNT;
    IF updated_reports <> 1 THEN
        RAISE EXCEPTION 'Weekly report is not ready for Telegram completion.' USING ERRCODE = '55000';
    END IF;
END;
$$;

CREATE OR REPLACE FUNCTION cti.fail_weekly_telegram_delivery(
    delivery_id_value bigint,
    error_code_value text,
    retry_safe_value boolean DEFAULT false
)
RETURNS void
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = cti, pg_temp
AS $$
DECLARE
    normalized_error text;
BEGIN
    normalized_error := regexp_replace(lower(coalesce(error_code_value, 'unknown')), '[^a-z0-9_.-]+', '_', 'g');
    normalized_error := left(trim(both '_' FROM normalized_error), 80);
    IF normalized_error = '' THEN normalized_error := 'unknown'; END IF;

    UPDATE cti.delivery_log AS delivery
    SET status = 'failed',
        error_code = (CASE WHEN retry_safe_value THEN 'retry_safe:' ELSE 'ambiguous:' END) || normalized_error,
        locked_at = NULL,
        attempted_at = now()
    WHERE delivery.id = delivery_id_value
      AND delivery.channel = 'telegram'
      AND delivery.status = 'queued';

    IF NOT FOUND THEN
        RAISE EXCEPTION 'Telegram delivery reservation is not active.' USING ERRCODE = '55000';
    END IF;
END;
$$;

CREATE OR REPLACE FUNCTION cti.telegram_article_lookup(
    action_value text,
    query_value text DEFAULT NULL,
    result_limit_value integer DEFAULT 5
)
RETURNS jsonb
LANGUAGE plpgsql
STABLE
SECURITY DEFINER
SET search_path = cti, pg_temp
AS $$
DECLARE
    normalized_action text := lower(trim(coalesce(action_value, '')));
    normalized_query text := lower(trim(coalesce(query_value, '')));
    result_limit integer := least(greatest(coalesce(result_limit_value, 5), 1), 5);
    article_results jsonb := '[]'::jsonb;
BEGIN
    IF normalized_action NOT IN ('menu', 'help', 'category', 'search') THEN
        RAISE EXCEPTION 'Unsupported Telegram CTI action.' USING ERRCODE = '22023';
    END IF;
    IF normalized_action = 'category'
       AND normalized_query NOT IN ('malware', 'vulnerability', 'data_breach', 'threat_intelligence', 'other') THEN
        RAISE EXCEPTION 'Unsupported Telegram CTI category.' USING ERRCODE = '22023';
    END IF;
    IF normalized_action = 'search' AND (
        char_length(normalized_query) NOT BETWEEN 2 AND 60
        OR normalized_query !~ '^[[:alnum:][:space:]._:/-]+$'
    ) THEN
        RAISE EXCEPTION 'Telegram CTI search query is invalid.' USING ERRCODE = '22023';
    END IF;

    IF normalized_action IN ('category', 'search') THEN
        SELECT coalesce(jsonb_agg(to_jsonb(selected_article) ORDER BY selected_article.rank_order), '[]'::jsonb)
        INTO article_results
        FROM (
            SELECT article.id AS article_id, left(article.title, 220) AS title,
                left(article.summary_tr, 700) AS summary_tr, article.category, article.severity,
                article.canonical_url, source.name AS source_name, article.published_at,
                row_number() OVER (ORDER BY
                    CASE article.severity WHEN 'critical' THEN 1 WHEN 'high' THEN 2
                        WHEN 'medium' THEN 3 WHEN 'low' THEN 4 ELSE 5 END,
                    article.published_at DESC, article.id DESC) AS rank_order
            FROM cti.articles AS article
            JOIN LATERAL (
                SELECT source.name
                FROM cti.article_occurrences AS occurrence
                JOIN cti.sources AS source ON source.id = occurrence.source_id
                WHERE occurrence.article_id = article.id
                ORDER BY occurrence.discovered_at, occurrence.id
                LIMIT 1
            ) AS source ON true
            WHERE article.analyzed_at IS NOT NULL AND article.summary_tr IS NOT NULL
              AND article.published_at >= now() - interval '8 days'
              AND ((normalized_action = 'category' AND article.category = normalized_query)
                OR (normalized_action = 'search'
                  AND to_tsvector('simple', article.title || ' ' || coalesce(article.summary_tr, ''))
                      @@ websearch_to_tsquery('simple', normalized_query)))
            ORDER BY CASE article.severity WHEN 'critical' THEN 1 WHEN 'high' THEN 2
                    WHEN 'medium' THEN 3 WHEN 'low' THEN 4 ELSE 5 END,
                article.published_at DESC, article.id DESC
            LIMIT result_limit
        ) AS selected_article;
    END IF;

    RETURN jsonb_build_object('action', normalized_action, 'query', nullif(normalized_query, ''),
        'articles', article_results, 'generated_at', now());
END;
$$;

CREATE TABLE IF NOT EXISTS cti.workflow_log (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    workflow_name text NOT NULL CHECK (char_length(workflow_name) BETWEEN 1 AND 200),
    level text NOT NULL CHECK (level IN ('info', 'warning', 'error')),
    event_code text NOT NULL CHECK (char_length(event_code) BETWEEN 1 AND 100),
    details jsonb NOT NULL DEFAULT '{}'::jsonb,
    occurred_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_workflow_log_occurred_at
    ON cti.workflow_log (occurred_at DESC);

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
    SET status = 'failed',
        locked_at = NULL,
        last_error_code = 'stale_lock_recovered'
    WHERE report.report_type = 'weekly'
      AND report.status = 'draft'
      AND report.locked_at < reference_time - interval '30 minutes';

    SELECT count(*) INTO total_daily_usage
    FROM cti.ai_usage AS usage
    WHERE usage.requested_at >= utc_day_start;

    SELECT count(*) INTO total_monthly_usage
    FROM cti.ai_usage AS usage
    WHERE usage.requested_at >= utc_month_start;

    SELECT count(*) INTO weekly_daily_usage
    FROM cti.ai_usage AS usage
    WHERE usage.purpose = 'weekly_report'
      AND usage.requested_at >= utc_day_start;

    SELECT count(*) INTO weekly_monthly_usage
    FROM cti.ai_usage AS usage
    WHERE usage.purpose = 'weekly_report'
      AND usage.requested_at >= utc_month_start;

    SELECT count(*) INTO active_analysis_reservations
    FROM cti.analysis_jobs AS job
    WHERE job.status = 'processing';

    IF weekly_daily_usage >= report_daily_limit_value
       OR weekly_monthly_usage >= report_monthly_limit_value
       OR total_daily_usage + active_analysis_reservations >= provider_daily_limit_value
       OR total_monthly_usage + active_analysis_reservations >= provider_monthly_limit_value THEN
        RETURN;
    END IF;

    SELECT report.*
    INTO selected_report
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
        SET status = 'draft',
            attempts = report.attempts + 1,
            locked_at = reference_time,
            last_error_code = NULL,
            generated_at = reference_time
        WHERE report.id = selected_report.id
        RETURNING report.* INTO selected_report;
    ELSE
        INSERT INTO cti.reports (
            report_type,
            window_start,
            window_end,
            status,
            title,
            content,
            generated_at,
            expires_at,
            attempts,
            locked_at
        )
        VALUES (
            'weekly',
            window_start_value,
            window_end_value,
            'draft',
            'Pending weekly CTI report',
            'Pending AI generation.',
            reference_time,
            reference_time + interval '8 weeks',
            1,
            reference_time
        )
        RETURNING * INTO selected_report;
    END IF;

    SELECT jsonb_agg(to_jsonb(candidate) ORDER BY candidate.severity_rank, candidate.published_at DESC)
    INTO selected_payload
    FROM (
        SELECT
            article.id AS article_id,
            article.title,
            article.category,
            article.severity,
            left(article.summary_tr, 800) AS summary_tr,
            article.analysis_confidence,
            article.published_at,
            article.canonical_url,
            selected_source.name AS source_name,
            CASE article.severity
                WHEN 'critical' THEN 1
                WHEN 'high' THEN 2
                WHEN 'medium' THEN 3
                WHEN 'low' THEN 4
                ELSE 5
            END AS severity_rank
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
    WHERE report.id = report_id_value
      AND report.report_type = 'weekly'
      AND report.status = 'draft'
      AND report.locked_at IS NOT NULL
    FOR UPDATE;

    IF NOT FOUND THEN
        RAISE EXCEPTION 'Weekly report reservation is not active.' USING ERRCODE = '55000';
    END IF;

    SELECT count(DISTINCT article.id)
    INTO valid_article_count
    FROM cti.articles AS article
    WHERE article.id = ANY(article_ids_value)
      AND article.analyzed_at IS NOT NULL
      AND article.published_at >= selected_report.window_start
      AND article.published_at < selected_report.window_end;

    IF valid_article_count <> cardinality(article_ids_value) THEN
        RAISE EXCEPTION 'Weekly report article selection is invalid.' USING ERRCODE = '22023';
    END IF;

    UPDATE cti.reports AS report
    SET status = 'ready',
        title = trim(title_value),
        content = trim(content_value),
        locked_at = NULL,
        last_error_code = NULL,
        generated_at = reference_time
    WHERE report.id = selected_report.id;

    INSERT INTO cti.report_articles (
        report_id,
        article_id,
        title_snapshot,
        url_snapshot,
        source_snapshot
    )
    SELECT
        selected_report.id,
        article.id,
        article.title,
        article.canonical_url,
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
    SET reported_at = reference_time,
        updated_at = reference_time
    WHERE article.id = ANY(article_ids_value);

    INSERT INTO cti.ai_usage (
        purpose,
        model,
        request_status,
        prompt_tokens,
        output_tokens,
        total_tokens,
        requested_at
    )
    VALUES (
        'weekly_report',
        trim(model_value),
        'success',
        prompt_tokens_value,
        output_tokens_value,
        total_tokens_value,
        reference_time
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
    SET status = 'failed',
        locked_at = NULL,
        last_error_code = trim(error_code_value),
        generated_at = reference_time
    WHERE report.id = report_id_value
      AND report.report_type = 'weekly'
      AND report.status = 'draft';
    GET DIAGNOSTICS updated_reports = ROW_COUNT;

    IF updated_reports <> 1 THEN
        RAISE EXCEPTION 'Weekly report reservation is not active.' USING ERRCODE = '55000';
    END IF;

    INSERT INTO cti.ai_usage (purpose, model, request_status, requested_at)
    VALUES (
        'weekly_report',
        trim(model_value),
        CASE WHEN rate_limited_value THEN 'rate_limited' ELSE 'failed' END,
        reference_time
    );
END;
$$;

CREATE OR REPLACE FUNCTION cti.apply_retention()
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, cti
AS $$
DECLARE
    reference_time timestamptz := clock_timestamp();
    cleaned_articles integer;
    deleted_fingerprints integer;
    deleted_reports integer;
    deleted_deliveries integer;
    deleted_ai_usage integer;
    deleted_info_logs integer;
    deleted_old_logs integer;
    deleted_articles integer;
BEGIN
    UPDATE cti.articles
    SET cleaned_content = NULL,
        updated_at = reference_time
    WHERE cleaned_content IS NOT NULL
      AND fetched_at < reference_time - interval '8 days'
      AND reported_at IS NOT NULL
      AND reported_at < reference_time - interval '24 hours';
    GET DIAGNOSTICS cleaned_articles = ROW_COUNT;

    DELETE FROM cti.fingerprints
    WHERE expires_at <= reference_time;
    GET DIAGNOSTICS deleted_fingerprints = ROW_COUNT;

    DELETE FROM cti.reports
    WHERE expires_at <= reference_time;
    GET DIAGNOSTICS deleted_reports = ROW_COUNT;

    DELETE FROM cti.delivery_log
    WHERE attempted_at < reference_time - interval '14 days';
    GET DIAGNOSTICS deleted_deliveries = ROW_COUNT;

    DELETE FROM cti.ai_usage
    WHERE requested_at < reference_time - interval '14 days';
    GET DIAGNOSTICS deleted_ai_usage = ROW_COUNT;

    DELETE FROM cti.workflow_log
    WHERE level = 'info'
      AND occurred_at < reference_time - interval '7 days';
    GET DIAGNOSTICS deleted_info_logs = ROW_COUNT;

    DELETE FROM cti.workflow_log
    WHERE occurred_at < reference_time - interval '14 days';
    GET DIAGNOSTICS deleted_old_logs = ROW_COUNT;

    DELETE FROM cti.articles
    WHERE fetched_at < reference_time - interval '30 days';
    GET DIAGNOSTICS deleted_articles = ROW_COUNT;

    RETURN jsonb_build_object(
        'cleaned_articles', cleaned_articles,
        'deleted_fingerprints', deleted_fingerprints,
        'deleted_reports', deleted_reports,
        'deleted_deliveries', deleted_deliveries,
        'deleted_ai_usage', deleted_ai_usage,
        'deleted_info_logs', deleted_info_logs,
        'deleted_old_logs', deleted_old_logs,
        'deleted_articles', deleted_articles
    );
END;
$$;

CREATE OR REPLACE FUNCTION cti.ingest_feed_item(
    source_id_value bigint,
    original_url_value text,
    title_value text,
    feed_guid_value text,
    published_at_value timestamptz
)
RETURNS TABLE(article_id bigint, is_new boolean, occurrence_added boolean)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, cti
AS $$
DECLARE
    source_record cti.sources%ROWTYPE;
    canonical_url_value text;
    hostname_value text;
    port_value text;
    normalized_title_value text;
    normalized_feed_guid_value text;
    title_hash_value char(64);
    url_hash_value char(64);
    existing_article_id bigint;
    inserted_occurrences integer;
    reference_time timestamptz := clock_timestamp();
BEGIN
    SELECT *
    INTO source_record
    FROM cti.sources AS source
    WHERE source.id = source_id_value
      AND source.enabled = true;

    IF NOT FOUND THEN
        RAISE EXCEPTION 'Unknown or disabled CTI source.' USING ERRCODE = '22023';
    END IF;

    canonical_url_value := split_part(trim(original_url_value), '#', 1);
    hostname_value := lower(substring(canonical_url_value FROM '^https://([^/:?#]+)'));
    port_value := substring(canonical_url_value FROM '^https://[^/:?#@]+:([0-9]+)');

    IF canonical_url_value = ''
       OR char_length(canonical_url_value) > 4000
       OR hostname_value IS NULL
       OR canonical_url_value ~ '^https://[^/?#]*@'
       OR (port_value IS NOT NULL AND port_value <> '443')
       OR NOT (hostname_value = ANY(source_record.allowed_hosts)) THEN
        RAISE EXCEPTION 'Article URL is not allowed for this CTI source.'
            USING ERRCODE = '22023';
    END IF;

    IF published_at_value IS NULL THEN
        RAISE EXCEPTION 'Article publication time is required.' USING ERRCODE = '22023';
    END IF;

    IF published_at_value < reference_time - interval '30 hours'
       OR published_at_value > reference_time + interval '15 minutes' THEN
        RETURN;
    END IF;

    IF title_value IS NULL OR char_length(trim(title_value)) NOT BETWEEN 1 AND 500 THEN
        RAISE EXCEPTION 'Article title length is invalid.' USING ERRCODE = '22023';
    END IF;

    normalized_feed_guid_value := NULLIF(trim(feed_guid_value), '');

    IF normalized_feed_guid_value IS NOT NULL
       AND char_length(normalized_feed_guid_value) > 1000 THEN
        RAISE EXCEPTION 'Feed GUID length is invalid.' USING ERRCODE = '22023';
    END IF;

    normalized_title_value := trim(regexp_replace(
        lower(title_value),
        '[^[:alnum:]]+',
        ' ',
        'g'
    ));

    IF normalized_title_value = '' THEN
        RAISE EXCEPTION 'Article title becomes empty after normalization.'
            USING ERRCODE = '22023';
    END IF;

    title_hash_value := encode(public.digest(normalized_title_value, 'sha256'), 'hex');
    url_hash_value := encode(public.digest(canonical_url_value, 'sha256'), 'hex');

    PERFORM pg_advisory_xact_lock(hashtextextended('url:' || url_hash_value, 0));
    PERFORM pg_advisory_xact_lock(hashtextextended('title:' || title_hash_value, 0));
    IF normalized_feed_guid_value IS NOT NULL THEN
        PERFORM pg_advisory_xact_lock(hashtextextended(
            'guid:' || source_id_value::text || ':' || normalized_feed_guid_value,
            0
        ));
    END IF;

    SELECT occurrence.article_id
    INTO existing_article_id
    FROM cti.article_occurrences AS occurrence
    WHERE occurrence.source_id = source_id_value
      AND (
          occurrence.original_url = canonical_url_value
          OR (
              normalized_feed_guid_value IS NOT NULL
              AND occurrence.feed_guid = normalized_feed_guid_value
          )
      )
    ORDER BY occurrence.id
    LIMIT 1;

    IF existing_article_id IS NULL THEN
        SELECT fingerprint.article_id
        INTO existing_article_id
        FROM cti.fingerprints AS fingerprint
        WHERE fingerprint.kind = 'url'
          AND fingerprint.fingerprint = url_hash_value
          AND fingerprint.expires_at > reference_time;
    END IF;

    IF existing_article_id IS NULL THEN
        SELECT article.id
        INTO existing_article_id
        FROM cti.articles AS article
        WHERE article.canonical_url = canonical_url_value;
    END IF;

    IF existing_article_id IS NULL THEN
        SELECT article.id
        INTO existing_article_id
        FROM cti.articles AS article
        WHERE article.title_hash = title_hash_value
          AND article.published_at BETWEEN
              published_at_value - interval '2 days'
              AND published_at_value + interval '2 days'
        ORDER BY article.fetched_at
        LIMIT 1;
    END IF;

    IF existing_article_id IS NULL THEN
        INSERT INTO cti.articles (
            canonical_url,
            title,
            normalized_title,
            title_hash,
            published_at
        )
        VALUES (
            canonical_url_value,
            trim(title_value),
            normalized_title_value,
            title_hash_value,
            published_at_value
        )
        ON CONFLICT (canonical_url) DO UPDATE
        SET updated_at = reference_time
        RETURNING id INTO existing_article_id;

        is_new := true;
    ELSE
        is_new := false;
    END IF;

    INSERT INTO cti.fingerprints (kind, fingerprint, article_id, expires_at)
    VALUES ('url', url_hash_value, existing_article_id, reference_time + interval '30 days')
    ON CONFLICT (kind, fingerprint) DO UPDATE
    SET article_id = EXCLUDED.article_id,
        expires_at = GREATEST(cti.fingerprints.expires_at, EXCLUDED.expires_at);

    INSERT INTO cti.article_occurrences (
        article_id,
        source_id,
        original_url,
        source_title,
        feed_guid,
        source_published_at
    )
    VALUES (
        existing_article_id,
        source_id_value,
        canonical_url_value,
        trim(title_value),
        normalized_feed_guid_value,
        published_at_value
    )
    ON CONFLICT DO NOTHING;
    GET DIAGNOSTICS inserted_occurrences = ROW_COUNT;

    article_id := existing_article_id;
    occurrence_added := inserted_occurrences = 1;
    RETURN NEXT;
END;
$$;

CREATE OR REPLACE FUNCTION cti.record_source_check(
    source_id_value bigint,
    success_value boolean,
    error_code_value text DEFAULT NULL
)
RETURNS void
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, cti
AS $$
DECLARE
    reference_time timestamptz := clock_timestamp();
BEGIN
    IF success_value THEN
        IF error_code_value IS NOT NULL THEN
            RAISE EXCEPTION 'A successful source check cannot include an error code.'
                USING ERRCODE = '22023';
        END IF;

        UPDATE cti.sources
        SET last_checked_at = reference_time,
            last_success_at = reference_time,
            last_error_code = NULL,
            updated_at = reference_time
        WHERE id = source_id_value
          AND enabled = true;
    ELSE
        IF error_code_value IS NULL OR error_code_value NOT IN (
            'feed_read_failed',
            'feed_item_invalid',
            'feed_item_store_failed'
        ) THEN
            RAISE EXCEPTION 'Invalid source check error code.' USING ERRCODE = '22023';
        END IF;

        UPDATE cti.sources
        SET last_checked_at = reference_time,
            last_error_at = reference_time,
            last_error_code = error_code_value,
            updated_at = reference_time
        WHERE id = source_id_value
          AND enabled = true;
    END IF;

    IF NOT FOUND THEN
        RAISE EXCEPTION 'Unknown or disabled CTI source.' USING ERRCODE = '22023';
    END IF;
END;
$$;

\set ON_ERROR_STOP on

CREATE TABLE IF NOT EXISTS cti.vulnerabilities (
    cve_id text PRIMARY KEY CHECK (cve_id ~ '^CVE-[0-9]{4}-[0-9]{4,}$'),
    kev_active boolean NOT NULL DEFAULT false,
    kev_vendor_project text CHECK (
        kev_vendor_project IS NULL OR char_length(kev_vendor_project) BETWEEN 1 AND 300
    ),
    kev_product text CHECK (
        kev_product IS NULL OR char_length(kev_product) BETWEEN 1 AND 500
    ),
    kev_vulnerability_name text CHECK (
        kev_vulnerability_name IS NULL OR char_length(kev_vulnerability_name) BETWEEN 1 AND 1000
    ),
    kev_date_added date,
    kev_short_description text CHECK (
        kev_short_description IS NULL OR char_length(kev_short_description) BETWEEN 1 AND 4000
    ),
    kev_required_action text CHECK (
        kev_required_action IS NULL OR char_length(kev_required_action) BETWEEN 1 AND 5000
    ),
    kev_due_date date,
    kev_known_ransomware_use text CHECK (
        kev_known_ransomware_use IS NULL OR kev_known_ransomware_use IN ('known', 'unknown')
    ),
    kev_notes text CHECK (kev_notes IS NULL OR char_length(kev_notes) <= 8000),
    kev_cwes text[] NOT NULL DEFAULT ARRAY[]::text[] CHECK (cardinality(kev_cwes) <= 50),
    kev_catalog_version text CHECK (
        kev_catalog_version IS NULL OR char_length(kev_catalog_version) BETWEEN 1 AND 100
    ),
    kev_catalog_released_at timestamptz,
    kev_last_synced_at timestamptz,
    epss_score numeric(10,9) CHECK (epss_score IS NULL OR epss_score BETWEEN 0 AND 1),
    epss_percentile numeric(10,9) CHECK (
        epss_percentile IS NULL OR epss_percentile BETWEEN 0 AND 1
    ),
    epss_score_date date,
    epss_last_synced_at timestamptz,
    epss_last_attempted_at timestamptz,
    last_article_seen_at timestamptz,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_vulnerabilities_kev_active
    ON cti.vulnerabilities (kev_date_added DESC, cve_id)
    WHERE kev_active = true;

CREATE INDEX IF NOT EXISTS ix_vulnerabilities_epss_priority
    ON cti.vulnerabilities (epss_percentile DESC, epss_score DESC)
    WHERE epss_score IS NOT NULL;

CREATE INDEX IF NOT EXISTS ix_vulnerabilities_epss_attempt_queue
    ON cti.vulnerabilities (epss_last_attempted_at, kev_active DESC, last_article_seen_at DESC);

CREATE TABLE IF NOT EXISTS cti.article_vulnerabilities (
    article_id bigint NOT NULL REFERENCES cti.articles(id) ON DELETE CASCADE,
    cve_id text NOT NULL REFERENCES cti.vulnerabilities(cve_id) ON DELETE RESTRICT,
    detection_method text NOT NULL DEFAULT 'rule' CHECK (detection_method IN ('rule', 'manual')),
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (article_id, cve_id)
);

CREATE INDEX IF NOT EXISTS ix_article_vulnerabilities_cve
    ON cti.article_vulnerabilities (cve_id, article_id);

CREATE TABLE IF NOT EXISTS cti.vulnerability_epss_history (
    cve_id text NOT NULL REFERENCES cti.vulnerabilities(cve_id) ON DELETE CASCADE,
    score_date date NOT NULL,
    epss_score numeric(10,9) NOT NULL CHECK (epss_score BETWEEN 0 AND 1),
    percentile numeric(10,9) NOT NULL CHECK (percentile BETWEEN 0 AND 1),
    fetched_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (cve_id, score_date)
);

CREATE INDEX IF NOT EXISTS ix_vulnerability_epss_history_date
    ON cti.vulnerability_epss_history (score_date DESC, cve_id);

CREATE OR REPLACE FUNCTION cti.sync_article_vulnerability_links()
RETURNS trigger
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, cti
AS $$
DECLARE
    reference_time timestamptz := clock_timestamp();
BEGIN
    INSERT INTO cti.vulnerabilities (cve_id, last_article_seen_at, created_at, updated_at)
    SELECT cve_id, reference_time, reference_time, reference_time
    FROM unnest(NEW.cves) AS cve(cve_id)
    ON CONFLICT (cve_id) DO UPDATE
    SET last_article_seen_at = GREATEST(
            COALESCE(cti.vulnerabilities.last_article_seen_at, EXCLUDED.last_article_seen_at),
            EXCLUDED.last_article_seen_at
        ),
        updated_at = reference_time;

    DELETE FROM cti.article_vulnerabilities AS link
    WHERE link.article_id = NEW.article_id
      AND NOT (link.cve_id = ANY(NEW.cves));

    INSERT INTO cti.article_vulnerabilities (
        article_id,
        cve_id,
        detection_method,
        created_at
    )
    SELECT NEW.article_id, cve_id, 'rule', reference_time
    FROM unnest(NEW.cves) AS cve(cve_id)
    ON CONFLICT (article_id, cve_id) DO UPDATE
    SET detection_method = EXCLUDED.detection_method;

    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_sync_article_vulnerability_links ON cti.article_rule_triage;
CREATE TRIGGER trg_sync_article_vulnerability_links
AFTER INSERT OR UPDATE OF cves ON cti.article_rule_triage
FOR EACH ROW
EXECUTE FUNCTION cti.sync_article_vulnerability_links();

INSERT INTO cti.vulnerabilities (cve_id, last_article_seen_at)
SELECT DISTINCT cve_id, triage.updated_at
FROM cti.article_rule_triage AS triage
CROSS JOIN LATERAL unnest(triage.cves) AS cve(cve_id)
ON CONFLICT (cve_id) DO UPDATE
SET last_article_seen_at = GREATEST(
        COALESCE(cti.vulnerabilities.last_article_seen_at, EXCLUDED.last_article_seen_at),
        EXCLUDED.last_article_seen_at
    ),
    updated_at = now();

INSERT INTO cti.article_vulnerabilities (article_id, cve_id, detection_method)
SELECT triage.article_id, cve_id, 'rule'
FROM cti.article_rule_triage AS triage
CROSS JOIN LATERAL unnest(triage.cves) AS cve(cve_id)
ON CONFLICT (article_id, cve_id) DO NOTHING;

WITH detected AS (
    SELECT DISTINCT
        article.id AS article_id,
        upper((capture.value)[1]) AS cve_id,
        article.updated_at
    FROM cti.articles AS article
    CROSS JOIN LATERAL regexp_matches(
        article.title || ' ' || COALESCE(article.cleaned_content, ''),
        '\m(CVE-[0-9]{4}-[0-9]{4,})\M',
        'gi'
    ) AS capture(value)
    WHERE article.published_at >= now() - interval '30 days'
)
INSERT INTO cti.vulnerabilities (cve_id, last_article_seen_at)
SELECT cve_id, max(updated_at)
FROM detected
GROUP BY cve_id
ON CONFLICT (cve_id) DO UPDATE
SET last_article_seen_at = GREATEST(
        COALESCE(cti.vulnerabilities.last_article_seen_at, EXCLUDED.last_article_seen_at),
        EXCLUDED.last_article_seen_at
    ),
    updated_at = now();

WITH detected AS (
    SELECT DISTINCT
        article.id AS article_id,
        upper((capture.value)[1]) AS cve_id
    FROM cti.articles AS article
    CROSS JOIN LATERAL regexp_matches(
        article.title || ' ' || COALESCE(article.cleaned_content, ''),
        '\m(CVE-[0-9]{4}-[0-9]{4,})\M',
        'gi'
    ) AS capture(value)
    WHERE article.published_at >= now() - interval '30 days'
)
INSERT INTO cti.article_vulnerabilities (article_id, cve_id, detection_method)
SELECT article_id, cve_id, 'rule'
FROM detected
ON CONFLICT (article_id, cve_id) DO NOTHING;

CREATE OR REPLACE FUNCTION cti.sync_cisa_kev_catalog(catalog_value jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, cti
AS $$
DECLARE
    reference_time timestamptz := clock_timestamp();
    catalog_version_value text;
    catalog_released_at_value timestamptz;
    declared_count integer;
    item_count integer;
    active_count integer;
BEGIN
    IF jsonb_typeof(catalog_value) IS DISTINCT FROM 'object' OR
       jsonb_typeof(catalog_value -> 'vulnerabilities') IS DISTINCT FROM 'array' THEN
        RAISE EXCEPTION 'CISA KEV payload shape is invalid.' USING ERRCODE = '22023';
    END IF;

    catalog_version_value := trim(catalog_value ->> 'catalogVersion');
    catalog_released_at_value := (catalog_value ->> 'dateReleased')::timestamptz;
    declared_count := (catalog_value ->> 'count')::integer;
    item_count := jsonb_array_length(catalog_value -> 'vulnerabilities');

    IF catalog_version_value IS NULL OR char_length(catalog_version_value) NOT BETWEEN 1 AND 100 OR
       item_count NOT BETWEEN 1 AND 5000 OR declared_count <> item_count OR
       catalog_released_at_value > reference_time + interval '1 day' THEN
        RAISE EXCEPTION 'CISA KEV catalog metadata is invalid.' USING ERRCODE = '22023';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM jsonb_array_elements(catalog_value -> 'vulnerabilities') AS entry(item)
        WHERE trim(item ->> 'cveID') !~ '^CVE-[0-9]{4}-[0-9]{4,}$'
           OR char_length(trim(item ->> 'vendorProject')) NOT BETWEEN 1 AND 300
           OR char_length(trim(item ->> 'product')) NOT BETWEEN 1 AND 500
           OR char_length(trim(item ->> 'vulnerabilityName')) NOT BETWEEN 1 AND 1000
           OR char_length(trim(item ->> 'shortDescription')) NOT BETWEEN 1 AND 4000
           OR char_length(trim(item ->> 'requiredAction')) NOT BETWEEN 1 AND 5000
           OR lower(trim(item ->> 'knownRansomwareCampaignUse')) NOT IN ('known', 'unknown')
           OR char_length(COALESCE(item ->> 'notes', '')) > 8000
           OR jsonb_typeof(COALESCE(item -> 'cwes', '[]'::jsonb)) IS DISTINCT FROM 'array'
           OR jsonb_array_length(COALESCE(item -> 'cwes', '[]'::jsonb)) > 50
    ) THEN
        RAISE EXCEPTION 'CISA KEV catalog contains an invalid item.' USING ERRCODE = '22023';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM (
            SELECT item ->> 'cveID' AS cve_id, count(*) AS occurrence_count
            FROM jsonb_array_elements(catalog_value -> 'vulnerabilities') AS entry(item)
            GROUP BY item ->> 'cveID'
            HAVING count(*) > 1
        ) AS duplicate
    ) THEN
        RAISE EXCEPTION 'CISA KEV catalog contains duplicate CVE identifiers.'
            USING ERRCODE = '22023';
    END IF;

    UPDATE cti.vulnerabilities
    SET kev_active = false,
        kev_last_synced_at = reference_time,
        updated_at = reference_time
    WHERE kev_active = true;

    INSERT INTO cti.vulnerabilities (
        cve_id,
        kev_active,
        kev_vendor_project,
        kev_product,
        kev_vulnerability_name,
        kev_date_added,
        kev_short_description,
        kev_required_action,
        kev_due_date,
        kev_known_ransomware_use,
        kev_notes,
        kev_cwes,
        kev_catalog_version,
        kev_catalog_released_at,
        kev_last_synced_at,
        created_at,
        updated_at
    )
    SELECT
        upper(trim(item."cveID")),
        true,
        trim(item."vendorProject"),
        trim(item.product),
        trim(item."vulnerabilityName"),
        item."dateAdded"::date,
        trim(item."shortDescription"),
        trim(item."requiredAction"),
        item."dueDate"::date,
        lower(trim(item."knownRansomwareCampaignUse")),
        NULLIF(trim(item.notes), ''),
        COALESCE(item.cwes, ARRAY[]::text[]),
        catalog_version_value,
        catalog_released_at_value,
        reference_time,
        reference_time,
        reference_time
    FROM jsonb_to_recordset(catalog_value -> 'vulnerabilities') AS item(
        "cveID" text,
        "vendorProject" text,
        product text,
        "vulnerabilityName" text,
        "dateAdded" text,
        "shortDescription" text,
        "requiredAction" text,
        "dueDate" text,
        "knownRansomwareCampaignUse" text,
        notes text,
        cwes text[]
    )
    ON CONFLICT (cve_id) DO UPDATE
    SET kev_active = true,
        kev_vendor_project = EXCLUDED.kev_vendor_project,
        kev_product = EXCLUDED.kev_product,
        kev_vulnerability_name = EXCLUDED.kev_vulnerability_name,
        kev_date_added = EXCLUDED.kev_date_added,
        kev_short_description = EXCLUDED.kev_short_description,
        kev_required_action = EXCLUDED.kev_required_action,
        kev_due_date = EXCLUDED.kev_due_date,
        kev_known_ransomware_use = EXCLUDED.kev_known_ransomware_use,
        kev_notes = EXCLUDED.kev_notes,
        kev_cwes = EXCLUDED.kev_cwes,
        kev_catalog_version = EXCLUDED.kev_catalog_version,
        kev_catalog_released_at = EXCLUDED.kev_catalog_released_at,
        kev_last_synced_at = reference_time,
        updated_at = reference_time;

    SELECT count(*) INTO active_count
    FROM cti.vulnerabilities
    WHERE kev_active = true;

    RETURN jsonb_build_object(
        'catalog_version', catalog_version_value,
        'catalog_released_at', catalog_released_at_value,
        'active_count', active_count,
        'synced_at', reference_time
    );
END;
$$;

CREATE OR REPLACE FUNCTION cti.get_epss_lookup_batch(batch_size_value integer DEFAULT 100)
RETURNS TABLE(cve_csv text, cve_count integer)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, cti
AS $$
BEGIN
    IF batch_size_value NOT BETWEEN 1 AND 100 THEN
        RAISE EXCEPTION 'EPSS batch size must be between 1 and 100.'
            USING ERRCODE = '22023';
    END IF;

    RETURN QUERY
    WITH selected AS (
        SELECT vulnerability.cve_id
        FROM cti.vulnerabilities AS vulnerability
        WHERE EXISTS (
            SELECT 1
            FROM cti.article_vulnerabilities AS link
            WHERE link.cve_id = vulnerability.cve_id
        )
          AND (
              vulnerability.epss_last_attempted_at IS NULL OR
              (vulnerability.epss_last_attempted_at AT TIME ZONE 'UTC')::date <
                  (clock_timestamp() AT TIME ZONE 'UTC')::date
          )
        ORDER BY
            (vulnerability.epss_last_attempted_at IS NULL) DESC,
            vulnerability.kev_active DESC,
            vulnerability.last_article_seen_at DESC NULLS LAST,
            vulnerability.cve_id
        LIMIT batch_size_value
    )
    SELECT string_agg(selected.cve_id, ',' ORDER BY selected.cve_id), count(*)::integer
    FROM selected
    HAVING count(*) > 0;
END;
$$;

CREATE OR REPLACE FUNCTION cti.record_epss_scores(
    response_value jsonb,
    requested_cves_value text[]
)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, cti
AS $$
DECLARE
    reference_time timestamptz := clock_timestamp();
    normalized_requested_cves text[];
    requested_count integer;
    item_count integer;
BEGIN
    IF requested_cves_value IS NULL OR cardinality(requested_cves_value) NOT BETWEEN 1 AND 100 THEN
        RAISE EXCEPTION 'EPSS request must contain between 1 and 100 CVE identifiers.'
            USING ERRCODE = '22023';
    END IF;

    SELECT array_agg(normalized.cve_id ORDER BY normalized.cve_id)
    INTO normalized_requested_cves
    FROM (
        SELECT upper(trim(requested.cve_id)) AS cve_id
        FROM unnest(requested_cves_value) AS requested(cve_id)
    ) AS normalized;

    requested_count := cardinality(normalized_requested_cves);

    IF EXISTS (
        SELECT 1
        FROM unnest(normalized_requested_cves) AS requested(cve_id)
        WHERE requested.cve_id !~ '^CVE-[0-9]{4}-[0-9]{4,}$'
    ) OR (
        SELECT count(DISTINCT requested.cve_id)
        FROM unnest(normalized_requested_cves) AS requested(cve_id)
    ) <> requested_count THEN
        RAISE EXCEPTION 'EPSS request contains an invalid or duplicate CVE identifier.'
            USING ERRCODE = '22023';
    END IF;

    IF (
        SELECT count(*)
        FROM cti.vulnerabilities AS vulnerability
        WHERE vulnerability.cve_id = ANY(normalized_requested_cves)
          AND EXISTS (
              SELECT 1
              FROM cti.article_vulnerabilities AS link
              WHERE link.cve_id = vulnerability.cve_id
          )
    ) <> requested_count THEN
        RAISE EXCEPTION 'EPSS request contains an untracked CVE identifier.'
            USING ERRCODE = '22023';
    END IF;

    IF jsonb_typeof(response_value) IS DISTINCT FROM 'object' OR
       response_value ->> 'status' IS DISTINCT FROM 'OK' OR
       jsonb_typeof(response_value -> 'data') IS DISTINCT FROM 'array' THEN
        RAISE EXCEPTION 'EPSS response shape is invalid.' USING ERRCODE = '22023';
    END IF;

    item_count := jsonb_array_length(response_value -> 'data');
    IF item_count > requested_count THEN
        RAISE EXCEPTION 'EPSS response exceeds the requested batch size.' USING ERRCODE = '22023';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM jsonb_array_elements(response_value -> 'data') AS entry(item)
        WHERE upper(trim(item ->> 'cve')) !~ '^CVE-[0-9]{4}-[0-9]{4,}$'
           OR NOT (upper(trim(item ->> 'cve')) = ANY(normalized_requested_cves))
           OR (item ->> 'epss')::numeric NOT BETWEEN 0 AND 1
           OR (item ->> 'percentile')::numeric NOT BETWEEN 0 AND 1
           OR (item ->> 'date')::date < DATE '2021-04-14'
           OR (item ->> 'date')::date > (reference_time AT TIME ZONE 'UTC')::date
    ) OR (
        SELECT count(DISTINCT upper(trim(entry.item ->> 'cve')))
        FROM jsonb_array_elements(response_value -> 'data') AS entry(item)
    ) <> item_count THEN
        RAISE EXCEPTION 'EPSS response contains an invalid, duplicate, or unrequested score.'
            USING ERRCODE = '22023';
    END IF;

    UPDATE cti.vulnerabilities
    SET epss_last_attempted_at = reference_time,
        updated_at = reference_time
    WHERE cve_id = ANY(normalized_requested_cves);

    INSERT INTO cti.vulnerability_epss_history (
        cve_id,
        score_date,
        epss_score,
        percentile,
        fetched_at
    )
    SELECT
        upper(trim(item.cve)),
        item."date"::date,
        item.epss::numeric,
        item.percentile::numeric,
        reference_time
    FROM jsonb_to_recordset(response_value -> 'data') AS item(
        cve text,
        epss text,
        percentile text,
        "date" text
    )
    ON CONFLICT (cve_id, score_date) DO UPDATE
    SET epss_score = EXCLUDED.epss_score,
        percentile = EXCLUDED.percentile,
        fetched_at = reference_time;

    UPDATE cti.vulnerabilities AS vulnerability
    SET epss_score = score.epss_score,
        epss_percentile = score.percentile,
        epss_score_date = score.score_date,
        epss_last_synced_at = reference_time,
        updated_at = reference_time
    FROM (
        SELECT
            upper(trim(item.cve)) AS cve_id,
            item.epss::numeric AS epss_score,
            item.percentile::numeric AS percentile,
            item."date"::date AS score_date
        FROM jsonb_to_recordset(response_value -> 'data') AS item(
            cve text,
            epss text,
            percentile text,
            "date" text
        )
    ) AS score
    WHERE vulnerability.cve_id = score.cve_id
      AND (
          vulnerability.epss_score_date IS NULL OR
          score.score_date >= vulnerability.epss_score_date
      );

    RETURN jsonb_build_object(
        'requested_count', requested_count,
        'recorded_count', item_count,
        'missing_count', requested_count - item_count,
        'attempted_at', reference_time
    );
END;
$$;

CREATE TABLE IF NOT EXISTS cti.events (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    event_type text NOT NULL CHECK (event_type IN ('vulnerability')),
    title text NOT NULL CHECK (char_length(title) BETWEEN 1 AND 500),
    status text NOT NULL DEFAULT 'active' CHECK (status IN ('active', 'quiet', 'closed')),
    clustering_version text NOT NULL CHECK (char_length(clustering_version) BETWEEN 1 AND 50),
    confidence numeric(4,3) NOT NULL CHECK (confidence BETWEEN 0 AND 1),
    first_seen_at timestamptz NOT NULL,
    last_seen_at timestamptz NOT NULL CHECK (last_seen_at >= first_seen_at),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_events_type_activity
    ON cti.events (event_type, last_seen_at DESC, id);

CREATE TABLE IF NOT EXISTS cti.event_vulnerabilities (
    event_id bigint NOT NULL REFERENCES cti.events(id) ON DELETE CASCADE,
    cve_id text NOT NULL REFERENCES cti.vulnerabilities(cve_id) ON DELETE RESTRICT,
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (event_id, cve_id)
);

CREATE INDEX IF NOT EXISTS ix_event_vulnerabilities_cve
    ON cti.event_vulnerabilities (cve_id, event_id);

CREATE TABLE IF NOT EXISTS cti.event_articles (
    event_id bigint NOT NULL REFERENCES cti.events(id) ON DELETE CASCADE,
    article_id bigint NOT NULL REFERENCES cti.articles(id) ON DELETE CASCADE,
    match_method text NOT NULL CHECK (match_method IN ('exact_cve')),
    match_confidence numeric(4,3) NOT NULL CHECK (match_confidence BETWEEN 0 AND 1),
    evidence jsonb NOT NULL CHECK (jsonb_typeof(evidence) = 'object'),
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (event_id, article_id)
);

CREATE INDEX IF NOT EXISTS ix_event_articles_article
    ON cti.event_articles (article_id, event_id);

CREATE OR REPLACE FUNCTION cti.assign_exact_cve_event(
    article_id_value bigint,
    cve_id_value text
)
RETURNS bigint
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, cti
AS $$
DECLARE
    reference_time timestamptz := clock_timestamp();
    normalized_cve text := upper(trim(cve_id_value));
    article_published_at timestamptz;
    article_title text;
    event_title text;
    event_id_value bigint;
BEGIN
    IF normalized_cve !~ '^CVE-[0-9]{4}-[0-9]{4,}$' THEN
        RAISE EXCEPTION 'Event CVE identifier is invalid.' USING ERRCODE = '22023';
    END IF;

    SELECT article.published_at, article.title
    INTO article_published_at, article_title
    FROM cti.articles AS article
    WHERE article.id = article_id_value;

    IF NOT FOUND THEN
        RAISE EXCEPTION 'Event article does not exist.' USING ERRCODE = '22023';
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM cti.article_vulnerabilities AS link
        WHERE link.article_id = article_id_value
          AND link.cve_id = normalized_cve
    ) THEN
        RAISE EXCEPTION 'The article is not linked to the requested CVE.'
            USING ERRCODE = '55000';
    END IF;

    PERFORM pg_advisory_xact_lock(hashtextextended('cti.exact_cve_event:' || normalized_cve, 0));

    SELECT event.id
    INTO event_id_value
    FROM cti.events AS event
    JOIN cti.event_vulnerabilities AS vulnerability
      ON vulnerability.event_id = event.id
    WHERE vulnerability.cve_id = normalized_cve
      AND event.event_type = 'vulnerability'
      AND event.clustering_version = 'exact-cve-v1'
      AND article_published_at BETWEEN
          event.first_seen_at - interval '14 days' AND
          event.last_seen_at + interval '14 days'
    ORDER BY
        CASE
            WHEN article_published_at < event.first_seen_at
                THEN event.first_seen_at - article_published_at
            WHEN article_published_at > event.last_seen_at
                THEN article_published_at - event.last_seen_at
            ELSE interval '0 seconds'
        END,
        event.id
    LIMIT 1
    FOR UPDATE OF event;

    IF event_id_value IS NULL THEN
        SELECT left(
            COALESCE(
                NULLIF(vulnerability.kev_vulnerability_name, ''),
                normalized_cve || ' — ' || article_title
            ),
            500
        )
        INTO event_title
        FROM cti.vulnerabilities AS vulnerability
        WHERE vulnerability.cve_id = normalized_cve;

        INSERT INTO cti.events (
            event_type,
            title,
            status,
            clustering_version,
            confidence,
            first_seen_at,
            last_seen_at,
            created_at,
            updated_at
        )
        VALUES (
            'vulnerability',
            COALESCE(event_title, normalized_cve || ' vulnerability activity'),
            'active',
            'exact-cve-v1',
            0.990,
            article_published_at,
            article_published_at,
            reference_time,
            reference_time
        )
        RETURNING id INTO event_id_value;

        INSERT INTO cti.event_vulnerabilities (event_id, cve_id, created_at)
        VALUES (event_id_value, normalized_cve, reference_time);
    END IF;

    INSERT INTO cti.event_articles (
        event_id,
        article_id,
        match_method,
        match_confidence,
        evidence,
        created_at
    )
    VALUES (
        event_id_value,
        article_id_value,
        'exact_cve',
        0.990,
        jsonb_build_object('cve_id', normalized_cve, 'window_days', 14),
        reference_time
    )
    ON CONFLICT (event_id, article_id) DO UPDATE
    SET match_confidence = GREATEST(
            cti.event_articles.match_confidence,
            EXCLUDED.match_confidence
        ),
        evidence = EXCLUDED.evidence;

    UPDATE cti.events
    SET first_seen_at = LEAST(first_seen_at, article_published_at),
        last_seen_at = GREATEST(last_seen_at, article_published_at),
        updated_at = reference_time
    WHERE id = event_id_value;

    RETURN event_id_value;
END;
$$;

CREATE OR REPLACE FUNCTION cti.cluster_article_vulnerability()
RETURNS trigger
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, cti
AS $$
BEGIN
    PERFORM cti.assign_exact_cve_event(NEW.article_id, NEW.cve_id);
    RETURN NEW;
END;
$$;

CREATE OR REPLACE FUNCTION cti.uncluster_article_vulnerability()
RETURNS trigger
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, cti
AS $$
BEGIN
    DELETE FROM cti.event_articles AS event_article
    USING cti.event_vulnerabilities AS event_vulnerability
    WHERE event_article.event_id = event_vulnerability.event_id
      AND event_article.article_id = OLD.article_id
      AND event_vulnerability.cve_id = OLD.cve_id;

    DELETE FROM cti.events AS event
    WHERE NOT EXISTS (
        SELECT 1
        FROM cti.event_articles AS event_article
        WHERE event_article.event_id = event.id
    );

    RETURN OLD;
END;
$$;

DROP TRIGGER IF EXISTS trg_cluster_article_vulnerability ON cti.article_vulnerabilities;
CREATE TRIGGER trg_cluster_article_vulnerability
AFTER INSERT ON cti.article_vulnerabilities
FOR EACH ROW
EXECUTE FUNCTION cti.cluster_article_vulnerability();

DROP TRIGGER IF EXISTS trg_uncluster_article_vulnerability ON cti.article_vulnerabilities;
CREATE TRIGGER trg_uncluster_article_vulnerability
AFTER DELETE ON cti.article_vulnerabilities
FOR EACH ROW
EXECUTE FUNCTION cti.uncluster_article_vulnerability();

DO $$
DECLARE
    link record;
BEGIN
    FOR link IN
        SELECT article_vulnerability.article_id, article_vulnerability.cve_id
        FROM cti.article_vulnerabilities AS article_vulnerability
        JOIN cti.articles AS article ON article.id = article_vulnerability.article_id
        ORDER BY article.published_at, article_vulnerability.article_id, article_vulnerability.cve_id
    LOOP
        PERFORM cti.assign_exact_cve_event(link.article_id, link.cve_id);
    END LOOP;
END;
$$;

REVOKE ALL ON FUNCTION cti.assign_exact_cve_event(bigint, text) FROM PUBLIC;
REVOKE ALL ON FUNCTION cti.cluster_article_vulnerability() FROM PUBLIC;
REVOKE ALL ON FUNCTION cti.uncluster_article_vulnerability() FROM PUBLIC;

REVOKE ALL ON SCHEMA cti FROM PUBLIC;
REVOKE ALL ON ALL TABLES IN SCHEMA cti FROM PUBLIC;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA cti FROM PUBLIC;
REVOKE ALL ON FUNCTION cti.apply_retention() FROM PUBLIC;
REVOKE ALL ON FUNCTION cti.ingest_feed_item(bigint, text, text, text, timestamptz)
    FROM PUBLIC;
REVOKE ALL ON FUNCTION cti.record_source_check(bigint, boolean, text) FROM PUBLIC;
REVOKE ALL ON FUNCTION cti.enqueue_article_analysis() FROM PUBLIC;
REVOKE ALL ON FUNCTION cti.claim_analysis_jobs(integer, integer, integer) FROM PUBLIC;
REVOKE ALL ON FUNCTION cti.record_article_rule_triage(bigint, jsonb) FROM PUBLIC;
REVOKE ALL ON FUNCTION cti.sync_article_vulnerability_links() FROM PUBLIC;
REVOKE ALL ON FUNCTION cti.sync_cisa_kev_catalog(jsonb) FROM PUBLIC;
REVOKE ALL ON FUNCTION cti.get_epss_lookup_batch(integer) FROM PUBLIC;
REVOKE ALL ON FUNCTION cti.record_epss_scores(jsonb, text[]) FROM PUBLIC;
REVOKE ALL ON FUNCTION cti.assign_exact_cve_event(bigint, text) FROM PUBLIC;
REVOKE ALL ON FUNCTION cti.cluster_article_vulnerability() FROM PUBLIC;
REVOKE ALL ON FUNCTION cti.uncluster_article_vulnerability() FROM PUBLIC;
REVOKE ALL ON FUNCTION cti.complete_article_analysis(
    bigint, text, text, text, text, numeric, text, integer, integer
) FROM PUBLIC;
REVOKE ALL ON FUNCTION cti.defer_article_analysis(
    bigint, text, boolean, boolean, text
) FROM PUBLIC;
REVOKE ALL ON FUNCTION cti.claim_weekly_report(
    timestamptz, timestamptz, integer, integer, integer, integer, integer
) FROM PUBLIC;
REVOKE ALL ON FUNCTION cti.complete_weekly_report(
    bigint, text, text, bigint[], text, integer, integer, integer
) FROM PUBLIC;
REVOKE ALL ON FUNCTION cti.fail_weekly_report(bigint, text, boolean, text)
    FROM PUBLIC;
REVOKE ALL ON FUNCTION cti.claim_weekly_telegram_delivery() FROM PUBLIC;
REVOKE ALL ON FUNCTION cti.complete_weekly_telegram_delivery(bigint, text) FROM PUBLIC;
REVOKE ALL ON FUNCTION cti.fail_weekly_telegram_delivery(bigint, text, boolean)
    FROM PUBLIC;
REVOKE ALL ON FUNCTION cti.telegram_article_lookup(text, text, integer)
    FROM PUBLIC;

GRANT CONNECT ON DATABASE cti TO cti_n8n;
GRANT USAGE ON SCHEMA cti TO cti_n8n;
GRANT SELECT, INSERT, UPDATE ON ALL TABLES IN SCHEMA cti TO cti_n8n;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA cti TO cti_n8n;
GRANT EXECUTE ON FUNCTION cti.apply_retention() TO cti_n8n;
GRANT EXECUTE ON FUNCTION cti.ingest_feed_item(bigint, text, text, text, timestamptz)
    TO cti_n8n;
GRANT EXECUTE ON FUNCTION cti.record_source_check(bigint, boolean, text) TO cti_n8n;
GRANT EXECUTE ON FUNCTION cti.claim_analysis_jobs(integer, integer, integer) TO cti_n8n;
GRANT EXECUTE ON FUNCTION cti.record_article_rule_triage(bigint, jsonb) TO cti_n8n;
GRANT EXECUTE ON FUNCTION cti.sync_cisa_kev_catalog(jsonb) TO cti_n8n;
GRANT EXECUTE ON FUNCTION cti.get_epss_lookup_batch(integer) TO cti_n8n;
GRANT EXECUTE ON FUNCTION cti.record_epss_scores(jsonb, text[]) TO cti_n8n;
GRANT EXECUTE ON FUNCTION cti.complete_article_analysis(
    bigint, text, text, text, text, numeric, text, integer, integer
) TO cti_n8n;
GRANT EXECUTE ON FUNCTION cti.defer_article_analysis(
    bigint, text, boolean, boolean, text
) TO cti_n8n;
GRANT EXECUTE ON FUNCTION cti.claim_weekly_report(
    timestamptz, timestamptz, integer, integer, integer, integer, integer
) TO cti_n8n;
GRANT EXECUTE ON FUNCTION cti.complete_weekly_report(
    bigint, text, text, bigint[], text, integer, integer, integer
) TO cti_n8n;
GRANT EXECUTE ON FUNCTION cti.fail_weekly_report(bigint, text, boolean, text)
    TO cti_n8n;
GRANT EXECUTE ON FUNCTION cti.claim_weekly_telegram_delivery() TO cti_n8n;
GRANT EXECUTE ON FUNCTION cti.complete_weekly_telegram_delivery(bigint, text)
    TO cti_n8n;
GRANT EXECUTE ON FUNCTION cti.fail_weekly_telegram_delivery(bigint, text, boolean)
    TO cti_n8n;
GRANT EXECUTE ON FUNCTION cti.telegram_article_lookup(text, text, integer)
    TO cti_n8n;

ALTER DEFAULT PRIVILEGES IN SCHEMA cti
    GRANT SELECT, INSERT, UPDATE ON TABLES TO cti_n8n;
ALTER DEFAULT PRIVILEGES IN SCHEMA cti
    GRANT USAGE, SELECT ON SEQUENCES TO cti_n8n;

REVOKE ALL ON cti.events, cti.event_vulnerabilities, cti.event_articles FROM cti_n8n;
REVOKE ALL ON SEQUENCE cti.events_id_seq FROM cti_n8n;

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
    ) AS source_names,
    ARRAY(
        SELECT link.cve_id
        FROM cti.article_vulnerabilities AS link
        WHERE link.article_id = article.id
        ORDER BY link.cve_id
    ) AS cve_ids,
    ARRAY(
        SELECT link.cve_id
        FROM cti.article_vulnerabilities AS link
        JOIN cti.vulnerabilities AS vulnerability ON vulnerability.cve_id = link.cve_id
        WHERE link.article_id = article.id
          AND vulnerability.kev_active = true
        ORDER BY link.cve_id
    ) AS kev_cve_ids,
    (
        SELECT max(vulnerability.epss_score)
        FROM cti.article_vulnerabilities AS link
        JOIN cti.vulnerabilities AS vulnerability ON vulnerability.cve_id = link.cve_id
        WHERE link.article_id = article.id
    ) AS max_epss_score,
    (
        SELECT max(vulnerability.epss_percentile)
        FROM cti.article_vulnerabilities AS link
        JOIN cti.vulnerabilities AS vulnerability ON vulnerability.cve_id = link.cve_id
        WHERE link.article_id = article.id
    ) AS max_epss_percentile
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

REVOKE ALL ON ALL TABLES IN SCHEMA cti FROM cti_dashboard;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA cti FROM cti_dashboard;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA cti FROM cti_dashboard;
REVOKE TEMPORARY ON DATABASE cti FROM cti_dashboard;

GRANT CONNECT ON DATABASE cti TO cti_dashboard;
GRANT USAGE ON SCHEMA cti TO cti_dashboard;
GRANT SELECT ON cti.dashboard_articles, cti.dashboard_reports, cti.dashboard_ai_usage TO cti_dashboard;

INSERT INTO cti.schema_versions (version)
VALUES (1)
ON CONFLICT (version) DO NOTHING;

INSERT INTO cti.schema_versions (version)
VALUES (2)
ON CONFLICT (version) DO NOTHING;

INSERT INTO cti.sources (
    name,
    feed_url,
    allowed_hosts,
    content_selector,
    trust_score,
    enabled
)
VALUES (
    'The Hacker News',
    'https://feeds.feedburner.com/TheHackersNews',
    ARRAY['thehackernews.com', 'www.thehackernews.com'],
    '#articlebody',
    80,
    true
)
ON CONFLICT (name) DO UPDATE
SET feed_url = EXCLUDED.feed_url,
    allowed_hosts = EXCLUDED.allowed_hosts,
    content_selector = EXCLUDED.content_selector,
    trust_score = EXCLUDED.trust_score,
    updated_at = now();

INSERT INTO cti.schema_versions (version)
VALUES (3)
ON CONFLICT (version) DO NOTHING;

INSERT INTO cti.schema_versions (version)
VALUES (4)
ON CONFLICT (version) DO NOTHING;

INSERT INTO cti.schema_versions (version)
VALUES (5)
ON CONFLICT (version) DO NOTHING;

INSERT INTO cti.schema_versions (version)
VALUES (6)
ON CONFLICT (version) DO NOTHING;

INSERT INTO cti.schema_versions (version)
VALUES (7)
ON CONFLICT (version) DO NOTHING;

INSERT INTO cti.schema_versions (version)
VALUES (8)
ON CONFLICT (version) DO NOTHING;

INSERT INTO cti.schema_versions (version)
VALUES (9)
ON CONFLICT (version) DO NOTHING;

INSERT INTO cti.schema_versions (version)
VALUES (10)
ON CONFLICT (version) DO NOTHING;

INSERT INTO cti.sources (
    name,
    feed_url,
    allowed_hosts,
    content_selector,
    trust_score,
    enabled
)
VALUES (
    'CISA Cybersecurity Advisories',
    'https://www.cisa.gov/cybersecurity-advisories/all.xml',
    ARRAY['cisa.gov', 'www.cisa.gov'],
    '.l-page-section--rich-text .l-page-section__content',
    95,
    true
)
ON CONFLICT (name) DO UPDATE
SET feed_url = EXCLUDED.feed_url,
    allowed_hosts = EXCLUDED.allowed_hosts,
    content_selector = EXCLUDED.content_selector,
    trust_score = EXCLUDED.trust_score,
    updated_at = now();

INSERT INTO cti.schema_versions (version)
VALUES (11)
ON CONFLICT (version) DO NOTHING;

INSERT INTO cti.schema_versions (version)
VALUES (12)
ON CONFLICT (version) DO NOTHING;

INSERT INTO cti.schema_versions (version)
VALUES (13)
ON CONFLICT (version) DO NOTHING;

INSERT INTO cti.schema_versions (version)
VALUES (14)
ON CONFLICT (version) DO NOTHING;

INSERT INTO cti.sources (
    name,
    feed_url,
    allowed_hosts,
    content_selector,
    trust_score,
    enabled
)
VALUES (
    'Microsoft Security Blog',
    'https://www.microsoft.com/en-us/security/blog/feed/',
    ARRAY['www.microsoft.com', 'azure.microsoft.com'],
    '.entry-content',
    90,
    true
)
ON CONFLICT (name) DO UPDATE
SET feed_url = EXCLUDED.feed_url,
    allowed_hosts = EXCLUDED.allowed_hosts,
    content_selector = EXCLUDED.content_selector,
    trust_score = EXCLUDED.trust_score,
    updated_at = now();

INSERT INTO cti.schema_versions (version)
VALUES (15)
ON CONFLICT (version) DO NOTHING;

INSERT INTO cti.schema_versions (version)
VALUES (16)
ON CONFLICT (version) DO NOTHING;

INSERT INTO cti.schema_versions (version)
VALUES (17)
ON CONFLICT (version) DO NOTHING;

INSERT INTO cti.schema_versions (version)
VALUES (18)
ON CONFLICT (version) DO NOTHING;

INSERT INTO cti.schema_versions (version)
VALUES (19)
ON CONFLICT (version) DO NOTHING;

INSERT INTO cti.schema_versions (version)
VALUES (20)
ON CONFLICT (version) DO NOTHING;

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

INSERT INTO cti.sources (
    name,
    feed_url,
    allowed_hosts,
    content_selector,
    trust_score,
    enabled
)
VALUES (
    'SecurityWeek',
    'https://www.securityweek.com/feed/',
    ARRAY['securityweek.com', 'www.securityweek.com'],
    '.entry-content',
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
VALUES (25)
ON CONFLICT (version) DO NOTHING;
