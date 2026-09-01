\set ON_ERROR_STOP on

BEGIN;

DO $$
DECLARE
    source_id_value bigint;
    failure_started_at timestamptz := clock_timestamp();
    first_article_id bigint;
    second_article_id bigint;
    first_batch record;
    second_batch record;
    first_cve text;
    result_value jsonb;
    unique_suffix text := txid_current()::text;
BEGIN
    SELECT id INTO source_id_value
    FROM cti.sources
    WHERE enabled = true
    ORDER BY id
    LIMIT 1;

    PERFORM cti.record_source_check(source_id_value, false, 'feed_read_failed');

    IF NOT EXISTS (
        SELECT 1
        FROM cti.sources
        WHERE id = source_id_value
          AND last_error_code = 'feed_read_failed'
          AND last_error_at >= failure_started_at
    ) THEN
        RAISE EXCEPTION 'A real source failure did not receive its own error timestamp.';
    END IF;

    PERFORM cti.record_source_check(source_id_value, true, NULL);

    IF NOT EXISTS (
        SELECT 1
        FROM cti.sources
        WHERE id = source_id_value
          AND last_error_code IS NULL
          AND last_success_at IS NOT NULL
    ) THEN
        RAISE EXCEPTION 'A successful source read did not clear the source error state.';
    END IF;

    UPDATE cti.vulnerabilities AS vulnerability
    SET epss_last_attempted_at = clock_timestamp()
    WHERE EXISTS (
        SELECT 1
        FROM cti.article_vulnerabilities AS link
        WHERE link.cve_id = vulnerability.cve_id
    );

    INSERT INTO cti.articles (
        canonical_url,
        title,
        normalized_title,
        title_hash,
        published_at
    )
    VALUES (
        'https://www.cisa.gov/epss-fairness-a-' || unique_suffix,
        'CVE-2099-91001 EPSS fairness test A',
        'cve 2099 91001 epss fairness test a',
        encode(digest('epss-fairness-a-' || unique_suffix, 'sha256'), 'hex'),
        clock_timestamp()
    )
    RETURNING id INTO first_article_id;

    INSERT INTO cti.articles (
        canonical_url,
        title,
        normalized_title,
        title_hash,
        published_at
    )
    VALUES (
        'https://www.cisa.gov/epss-fairness-b-' || unique_suffix,
        'CVE-2099-91002 EPSS fairness test B',
        'cve 2099 91002 epss fairness test b',
        encode(digest('epss-fairness-b-' || unique_suffix, 'sha256'), 'hex'),
        clock_timestamp()
    )
    RETURNING id INTO second_article_id;

    INSERT INTO cti.vulnerabilities (cve_id, last_article_seen_at)
    VALUES
        ('CVE-2099-91001', clock_timestamp()),
        ('CVE-2099-91002', clock_timestamp());

    INSERT INTO cti.article_vulnerabilities (article_id, cve_id, detection_method)
    VALUES
        (first_article_id, 'CVE-2099-91001', 'manual'),
        (second_article_id, 'CVE-2099-91002', 'manual');

    SELECT * INTO first_batch
    FROM cti.get_epss_lookup_batch(1);

    IF first_batch.cve_count <> 1 OR first_batch.cve_csv NOT IN ('CVE-2099-91001', 'CVE-2099-91002') THEN
        RAISE EXCEPTION 'The first fair EPSS batch was not selected correctly.';
    END IF;

    first_cve := first_batch.cve_csv;
    SELECT cti.record_epss_scores(
        jsonb_build_object('status', 'OK', 'data', jsonb_build_array()),
        ARRAY[first_cve]
    ) INTO result_value;

    IF (result_value ->> 'requested_count')::integer <> 1 OR
       (result_value ->> 'recorded_count')::integer <> 0 OR
       (result_value ->> 'missing_count')::integer <> 1 OR NOT EXISTS (
           SELECT 1
           FROM cti.vulnerabilities
           WHERE cve_id = first_cve
             AND epss_last_attempted_at IS NOT NULL
       ) THEN
        RAISE EXCEPTION 'An EPSS response without a score did not record the lookup attempt.';
    END IF;

    SELECT * INTO second_batch
    FROM cti.get_epss_lookup_batch(1);

    IF second_batch.cve_count <> 1 OR
       second_batch.cve_csv = first_cve OR
       second_batch.cve_csv NOT IN ('CVE-2099-91001', 'CVE-2099-91002') THEN
        RAISE EXCEPTION 'An unanswered EPSS lookup starved the next CVE in the queue.';
    END IF;

    IF to_regprocedure('cti.record_epss_scores(jsonb)') IS NOT NULL OR
       NOT has_function_privilege(
           'cti_n8n',
           'cti.record_epss_scores(jsonb,text[])',
           'EXECUTE'
       ) THEN
        RAISE EXCEPTION 'The EPSS writer function surface is not restricted correctly.';
    END IF;
END;
$$;

ROLLBACK;
