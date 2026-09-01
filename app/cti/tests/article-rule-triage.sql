\set ON_ERROR_STOP on

BEGIN;

DO $$
DECLARE
    article_id_value bigint;
    unique_suffix text := txid_current()::text;
BEGIN
    INSERT INTO cti.articles (
        canonical_url,
        title,
        normalized_title,
        title_hash,
        published_at
    )
    VALUES (
        'https://www.cisa.gov/rule-triage-test-' || unique_suffix,
        'CVE-2026-12345 actively exploited vulnerability test',
        'cve 2026 12345 actively exploited vulnerability test',
        encode(digest('rule-triage-test-' || unique_suffix, 'sha256'), 'hex'),
        clock_timestamp()
    )
    RETURNING id INTO article_id_value;

    UPDATE cti.analysis_jobs
    SET status = 'processing',
        attempts = 1,
        locked_at = clock_timestamp(),
        updated_at = clock_timestamp()
    WHERE article_id = article_id_value;

    PERFORM cti.record_article_rule_triage(
        article_id_value,
        jsonb_build_object(
            'ruleset_version', 'cti-rules-v1',
            'cves', jsonb_build_array('cve-2026-12345', 'CVE-2026-12345'),
            'cvss_max', 9.8,
            'has_active_exploitation', true,
            'has_patch', true,
            'has_poc', false,
            'detected_category', 'vulnerability',
            'detected_severity', 'critical',
            'rule_summary', 'A critical vulnerability is actively exploited and a patch is available.',
            'rule_confidence', 0.960,
            'priority_score', 100,
            'source_trust_score', 95,
            'ai_recommendation', 'candidate_bypass',
            'decision_reasons', jsonb_build_array(
                'cve_detected',
                'active_exploitation',
                'patch_available',
                'high_trust_source'
            )
        )
    );

    IF NOT EXISTS (
        SELECT 1
        FROM cti.article_rule_triage
        WHERE article_id = article_id_value
          AND ruleset_version = 'cti-rules-v1'
          AND cves = ARRAY['CVE-2026-12345']
          AND cvss_max = 9.8
          AND detected_category = 'vulnerability'
          AND detected_severity = 'critical'
          AND ai_recommendation = 'candidate_bypass'
          AND decision_reasons @> ARRAY['active_exploitation', 'cve_detected']
    ) THEN
        RAISE EXCEPTION 'Valid rule triage was not stored or normalized.';
    END IF;

    BEGIN
        PERFORM cti.record_article_rule_triage(
            article_id_value,
            jsonb_build_object(
                'ruleset_version', 'cti-rules-v1',
                'cves', jsonb_build_array('NOT-A-CVE'),
                'cvss_max', NULL,
                'has_active_exploitation', false,
                'has_patch', false,
                'has_poc', false,
                'detected_category', 'other',
                'detected_severity', 'unknown',
                'rule_summary', 'This payload should be rejected because the CVE identifier is invalid.',
                'rule_confidence', 0.500,
                'priority_score', 20,
                'source_trust_score', 50,
                'ai_recommendation', 'required',
                'decision_reasons', jsonb_build_array('ambiguous_category')
            )
        );
        RAISE EXCEPTION 'An invalid CVE identifier was accepted.';
    EXCEPTION
        WHEN SQLSTATE '22023' THEN
            NULL;
    END;

    IF NOT has_function_privilege(
        'cti_n8n',
        'cti.record_article_rule_triage(bigint,jsonb)',
        'EXECUTE'
    ) THEN
        RAISE EXCEPTION 'The n8n role cannot record rule triage.';
    END IF;
END;
$$;

ROLLBACK;
