\set ON_ERROR_STOP on

ALTER TABLE cti.sources
    ADD COLUMN IF NOT EXISTS last_error_at timestamptz,
    ADD COLUMN IF NOT EXISTS last_error_code text;

ALTER TABLE cti.sources
    DROP CONSTRAINT IF EXISTS sources_last_error_code_check;

ALTER TABLE cti.sources
    ADD CONSTRAINT sources_last_error_code_check CHECK (
        last_error_code IS NULL OR last_error_code IN (
            'feed_read_failed',
            'feed_item_invalid',
            'feed_item_store_failed'
        )
    );

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

REVOKE ALL ON FUNCTION cti.record_source_check(bigint, boolean, text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION cti.record_source_check(bigint, boolean, text) TO cti_n8n;

INSERT INTO cti.schema_versions (version)
VALUES (12)
ON CONFLICT (version) DO NOTHING;
