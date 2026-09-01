\set ON_ERROR_STOP on

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
        article_id, ruleset_version, cves, cvss_max,
        has_active_exploitation, has_patch, has_poc,
        detected_category, detected_severity, rule_summary,
        rule_confidence, priority_score, source_trust_score,
        ai_recommendation, decision_reasons, created_at, updated_at
    )
    VALUES (
        article_id_value, ruleset_version_value, cves_value, cvss_max_value,
        has_active_exploitation_value, has_patch_value, has_poc_value,
        detected_category_value, detected_severity_value, rule_summary_value,
        rule_confidence_value, priority_score_value, source_trust_score_value,
        ai_recommendation_value, decision_reasons_value, reference_time, reference_time
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

REVOKE ALL ON FUNCTION cti.record_article_rule_triage(bigint, jsonb) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION cti.record_article_rule_triage(bigint, jsonb) TO cti_n8n;

INSERT INTO cti.schema_versions (version)
VALUES (17)
ON CONFLICT (version) DO NOTHING;
