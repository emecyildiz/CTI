\set ON_ERROR_STOP on

BEGIN;

INSERT INTO cti.sources (
    name,
    feed_url,
    allowed_hosts,
    content_selector,
    trust_score
)
VALUES (
    'Weekly Report Test Source',
    'https://feeds.example.test/weekly.xml',
    ARRAY['weekly.example.test'],
    'article',
    90
)
ON CONFLICT (name) DO NOTHING;

DO $$
DECLARE
    source_id_value bigint;
    article_id_value bigint;
    first_window_end timestamptz := date_trunc('minute', clock_timestamp());
    first_window_start timestamptz;
    second_window_end timestamptz;
    second_window_start timestamptz;
    claimed_record record;
    selected_ids bigint[];
    result_count integer;
BEGIN
    first_window_start := first_window_end - interval '7 days';
    second_window_end := first_window_end - interval '1 minute';
    second_window_start := second_window_end - interval '7 days';

    SELECT id INTO source_id_value
    FROM cti.sources
    WHERE name = 'Weekly Report Test Source';

    SELECT article_id INTO article_id_value
    FROM cti.ingest_feed_item(
        source_id_value,
        'https://weekly.example.test/reports/test-article',
        'Weekly Report Guard Test Article',
        'weekly-report-test-guid',
        first_window_end - interval '2 hours'
    );

    UPDATE cti.articles
    SET category = 'vulnerability',
        severity = 'high',
        summary_tr = 'Haftalık rapor rezervasyon testi için kısa ve doğrulanmış özet.',
        analysis_confidence = 0.950,
        analyzed_at = clock_timestamp()
    WHERE id = article_id_value;

    SELECT * INTO claimed_record
    FROM cti.claim_weekly_report(
        first_window_start,
        first_window_end,
        5,
        31,
        100,
        5000,
        40
    );

    IF claimed_record.report_id IS NULL
       OR jsonb_array_length(claimed_record.article_payload) < 1
       OR NOT EXISTS (
           SELECT 1
           FROM jsonb_array_elements(claimed_record.article_payload) AS payload_item
           WHERE (payload_item->>'article_id')::bigint = article_id_value
       ) THEN
        RAISE EXCEPTION 'Weekly report reservation did not return the expected article.';
    END IF;

    SELECT count(*) INTO result_count
    FROM cti.claim_weekly_report(
        first_window_start,
        first_window_end,
        5,
        31,
        100,
        5000,
        40
    );
    IF result_count <> 0 THEN
        RAISE EXCEPTION 'The same weekly report window was reserved twice.';
    END IF;

    selected_ids := ARRAY[article_id_value];
    PERFORM cti.complete_weekly_report(
        claimed_record.report_id,
        'Weekly CTI Test Report',
        'Deterministic weekly report test content.',
        selected_ids,
        'models/test-model',
        10,
        5,
        15
    );

    IF NOT EXISTS (
        SELECT 1 FROM cti.reports
        WHERE id = claimed_record.report_id AND status = 'ready'
    ) OR NOT EXISTS (
        SELECT 1 FROM cti.report_articles
        WHERE report_id = claimed_record.report_id AND article_id = article_id_value
    ) OR NOT EXISTS (
        SELECT 1 FROM cti.ai_usage
        WHERE purpose = 'weekly_report' AND request_status = 'success'
    ) THEN
        RAISE EXCEPTION 'Completed weekly report state was not persisted.';
    END IF;

    SELECT * INTO claimed_record
    FROM cti.claim_weekly_report(
        second_window_start,
        second_window_end,
        5,
        31,
        100,
        5000,
        40
    );

    IF claimed_record.report_id IS NULL THEN
        RAISE EXCEPTION 'Failure-path weekly report reservation was not created.';
    END IF;

    PERFORM cti.fail_weekly_report(
        claimed_record.report_id,
        'models/test-model',
        false,
        'test_failure'
    );

    IF NOT EXISTS (
        SELECT 1 FROM cti.reports
        WHERE id = claimed_record.report_id
          AND status = 'failed'
          AND last_error_code = 'test_failure'
    ) OR NOT EXISTS (
        SELECT 1 FROM cti.ai_usage
        WHERE purpose = 'weekly_report' AND request_status = 'failed'
    ) THEN
        RAISE EXCEPTION 'Failed weekly report state was not persisted.';
    END IF;
END;
$$;

ROLLBACK;

SELECT 'CTI weekly report tests passed.' AS result;
