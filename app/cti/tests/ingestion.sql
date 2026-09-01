\set ON_ERROR_STOP on

INSERT INTO cti.sources (
    name,
    feed_url,
    allowed_hosts,
    content_selector,
    trust_score
)
VALUES (
    'Test Source',
    'https://feeds.example.test/security.xml',
    ARRAY['news.example.test'],
    'article',
    80
);

DO $$
DECLARE
    source_id_value bigint;
    result_record record;
    result_count integer;
BEGIN
    SELECT id INTO source_id_value
    FROM cti.sources
    WHERE name = 'Test Source';

    PERFORM cti.record_source_check(source_id_value, false, 'feed_read_failed');

    IF NOT EXISTS (
        SELECT 1
        FROM cti.sources
        WHERE id = source_id_value
          AND last_checked_at IS NOT NULL
          AND last_error_at IS NULL
          AND last_error_code = 'feed_read_failed'
          AND last_success_at IS NULL
    ) THEN
        RAISE EXCEPTION 'Provisional source failure was not recorded safely.';
    END IF;

    PERFORM cti.record_source_check(source_id_value, false, 'feed_read_failed');

    IF NOT EXISTS (
        SELECT 1
        FROM cti.sources
        WHERE id = source_id_value
          AND last_checked_at IS NOT NULL
          AND last_error_at IS NOT NULL
          AND last_error_code = 'feed_read_failed'
          AND last_success_at IS NULL
    ) THEN
        RAISE EXCEPTION 'Previous source failure was not finalized.';
    END IF;

    PERFORM cti.record_source_check(source_id_value, true, NULL);

    IF NOT EXISTS (
        SELECT 1
        FROM cti.sources
        WHERE id = source_id_value
          AND last_checked_at IS NOT NULL
          AND last_success_at IS NOT NULL
          AND last_error_at IS NOT NULL
          AND last_error_code IS NULL
    ) THEN
        RAISE EXCEPTION 'Successful source health was not recorded.';
    END IF;

    SELECT * INTO result_record
    FROM cti.ingest_feed_item(
        source_id_value,
        'https://news.example.test/advisories/one#details',
        'Critical Product Vulnerability',
        'guid-one',
        now() - interval '1 hour'
    );

    IF result_record.is_new IS DISTINCT FROM true
       OR result_record.occurrence_added IS DISTINCT FROM true THEN
        RAISE EXCEPTION 'First ingestion did not create an article and occurrence.';
    END IF;

    SELECT * INTO result_record
    FROM cti.ingest_feed_item(
        source_id_value,
        'https://news.example.test/advisories/one',
        'Critical Product Vulnerability',
        'guid-one',
        now() - interval '1 hour'
    );

    IF result_record.is_new IS DISTINCT FROM false
       OR result_record.occurrence_added IS DISTINCT FROM false THEN
        RAISE EXCEPTION 'Repeated URL was not deduplicated.';
    END IF;

    SELECT * INTO result_record
    FROM cti.ingest_feed_item(
        source_id_value,
        'https://news.example.test/advisories/syndicated-copy',
        'Critical Product Vulnerability',
        'guid-two',
        now() - interval '50 minutes'
    );

    IF result_record.is_new IS DISTINCT FROM false
       OR result_record.occurrence_added IS DISTINCT FROM true THEN
        RAISE EXCEPTION 'Exact-title syndicated occurrence was not merged.';
    END IF;

    SELECT * INTO result_record
    FROM cti.ingest_feed_item(
        source_id_value,
        'https://news.example.test/advisories/guid-moved',
        'A Changed Feed Title',
        'guid-two',
        now() - interval '40 minutes'
    );

    IF result_record.is_new IS DISTINCT FROM false
       OR result_record.occurrence_added IS DISTINCT FROM false THEN
        RAISE EXCEPTION 'Repeated feed GUID was not deduplicated.';
    END IF;

    SELECT count(*) INTO result_count
    FROM cti.ingest_feed_item(
        source_id_value,
        'https://news.example.test/advisories/old',
        'Old Security Story',
        'guid-old',
        now() - interval '31 hours'
    );

    IF result_count <> 0 THEN
        RAISE EXCEPTION 'An article outside the 30-hour window was accepted.';
    END IF;

    BEGIN
        PERFORM * FROM cti.ingest_feed_item(
            source_id_value,
            'https://attacker.example/advisories/one',
            'Disallowed Host Story',
            'guid-host',
            now()
        );
        RAISE EXCEPTION 'A disallowed article host was accepted.';
    EXCEPTION
        WHEN SQLSTATE '22023' THEN
            NULL;
    END;

    BEGIN
        PERFORM * FROM cti.ingest_feed_item(
            source_id_value,
            'https://news.example.test:8443/advisories/one',
            'Non-default Port Story',
            'guid-port',
            now()
        );
        RAISE EXCEPTION 'A non-default HTTPS port was accepted.';
    EXCEPTION
        WHEN SQLSTATE '22023' THEN
            NULL;
    END;

    BEGIN
        PERFORM * FROM cti.ingest_feed_item(
            source_id_value,
            'https://user@news.example.test/advisories/one',
            'Embedded Credentials Story',
            'guid-userinfo',
            now()
        );
        RAISE EXCEPTION 'A URL containing user information was accepted.';
    EXCEPTION
        WHEN SQLSTATE '22023' THEN
            NULL;
    END;

    IF (SELECT count(*) FROM cti.articles) <> 1 THEN
        RAISE EXCEPTION 'Deduplication left an unexpected article count.';
    END IF;

    IF (SELECT count(*) FROM cti.article_occurrences) <> 2 THEN
        RAISE EXCEPTION 'Deduplication left an unexpected occurrence count.';
    END IF;

    IF (SELECT count(*) FROM cti.analysis_jobs) <> 1 THEN
        RAISE EXCEPTION 'A new article did not receive exactly one analysis job.';
    END IF;

    IF has_table_privilege('cti_n8n', 'cti.articles', 'DELETE') THEN
        RAISE EXCEPTION 'The n8n role unexpectedly has direct DELETE permission.';
    END IF;

    IF NOT has_function_privilege(
        'cti_n8n',
        'cti.ingest_feed_item(bigint,text,text,text,timestamp with time zone)',
        'EXECUTE'
    ) THEN
        RAISE EXCEPTION 'The n8n role cannot execute the ingestion function.';
    END IF;
END;
$$;

DO $$
DECLARE
    claimed_record record;
    claimed_count integer;
BEGIN
    SELECT * INTO claimed_record
    FROM cti.claim_analysis_jobs(1, 20, 400);

    IF claimed_record.article_id IS NULL OR claimed_record.attempt <> 1 THEN
        RAISE EXCEPTION 'The analysis queue did not claim the pending article.';
    END IF;

    SELECT count(*) INTO claimed_count
    FROM cti.claim_analysis_jobs(1, 20, 400);

    IF claimed_count <> 0 THEN
        RAISE EXCEPTION 'A processing analysis job was claimed twice.';
    END IF;

    PERFORM cti.complete_article_analysis(
        claimed_record.article_id,
        'vulnerability',
        'high',
        'Test Turkish summary.',
        'Test cleaned article content.',
        0.950,
        'test-model',
        100,
        25
    );

    IF NOT EXISTS (
        SELECT 1
        FROM cti.analysis_jobs
        WHERE article_id = claimed_record.article_id
          AND status = 'completed'
    ) THEN
        RAISE EXCEPTION 'The completed analysis job state was not persisted.';
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM cti.ai_usage
        WHERE article_id = claimed_record.article_id
          AND request_status = 'success'
          AND total_tokens = 125
    ) THEN
        RAISE EXCEPTION 'Successful AI usage was not recorded.';
    END IF;

    PERFORM cti.ingest_feed_item(
        (SELECT id FROM cti.sources WHERE name = 'Test Source'),
        'https://news.example.test/advisories/monthly-cap',
        'Monthly Capacity Test',
        'guid-monthly-cap',
        now()
    );

    SELECT count(*) INTO claimed_count
    FROM cti.claim_analysis_jobs(1, 1, 1);

    IF claimed_count <> 0 THEN
        RAISE EXCEPTION 'The monthly AI request ceiling was not enforced.';
    END IF;
END;
$$;

SELECT 'CTI ingestion tests passed.' AS result;
