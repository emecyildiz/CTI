\set ON_ERROR_STOP on

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

ALTER TABLE cti.vulnerabilities
    ADD COLUMN IF NOT EXISTS epss_last_attempted_at timestamptz;

CREATE INDEX IF NOT EXISTS ix_vulnerabilities_epss_attempt_queue
    ON cti.vulnerabilities (epss_last_attempted_at, kev_active DESC, last_article_seen_at DESC);

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

DROP FUNCTION cti.record_epss_scores(jsonb);

CREATE FUNCTION cti.record_epss_scores(
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

REVOKE ALL ON FUNCTION cti.record_source_check(bigint, boolean, text) FROM PUBLIC;
REVOKE ALL ON FUNCTION cti.get_epss_lookup_batch(integer) FROM PUBLIC;
REVOKE ALL ON FUNCTION cti.record_epss_scores(jsonb, text[]) FROM PUBLIC;

GRANT EXECUTE ON FUNCTION cti.record_source_check(bigint, boolean, text) TO cti_n8n;
GRANT EXECUTE ON FUNCTION cti.get_epss_lookup_batch(integer) TO cti_n8n;
GRANT EXECUTE ON FUNCTION cti.record_epss_scores(jsonb, text[]) TO cti_n8n;

INSERT INTO cti.schema_versions (version)
VALUES (20)
ON CONFLICT (version) DO NOTHING;
