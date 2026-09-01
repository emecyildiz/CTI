\set ON_ERROR_STOP on

CREATE EXTENSION IF NOT EXISTS pgcrypto;

ALTER TABLE cti.article_occurrences
    DROP CONSTRAINT IF EXISTS article_occurrences_original_url_check;
ALTER TABLE cti.article_occurrences
    ADD CONSTRAINT article_occurrences_original_url_check CHECK (
        original_url ~ '^https://' AND char_length(original_url) <= 4000
    );

ALTER TABLE cti.article_occurrences
    DROP CONSTRAINT IF EXISTS article_occurrences_feed_guid_check;
ALTER TABLE cti.article_occurrences
    ADD CONSTRAINT article_occurrences_feed_guid_check CHECK (
        feed_guid IS NULL OR char_length(feed_guid) <= 1000
    );

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

    IF canonical_url_value = ''
       OR char_length(canonical_url_value) > 4000
       OR hostname_value IS NULL
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

REVOKE ALL ON FUNCTION cti.ingest_feed_item(bigint, text, text, text, timestamptz)
    FROM PUBLIC;
GRANT EXECUTE ON FUNCTION cti.ingest_feed_item(bigint, text, text, text, timestamptz)
    TO cti_n8n;

INSERT INTO cti.schema_versions (version)
VALUES (2)
ON CONFLICT (version) DO NOTHING;
