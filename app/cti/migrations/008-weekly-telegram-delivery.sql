BEGIN;

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
        ALTER TABLE cti.delivery_log ADD CONSTRAINT delivery_log_attempts_check
            CHECK (attempts BETWEEN 0 AND 3);
    END IF;
END;
$$;

CREATE UNIQUE INDEX IF NOT EXISTS ux_delivery_log_report_channel
    ON cti.delivery_log (report_id, channel)
    WHERE report_id IS NOT NULL;

CREATE OR REPLACE FUNCTION cti.claim_weekly_telegram_delivery()
RETURNS TABLE (delivery_id bigint, report_id bigint, report_title text, report_content text)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = cti, pg_temp AS $$
DECLARE
    reference_time timestamptz := now();
    selected_report_id bigint;
BEGIN
    PERFORM pg_advisory_xact_lock(hashtextextended('cti.claim_weekly_telegram_delivery', 0));
    UPDATE cti.delivery_log AS delivery
    SET status = 'failed', error_code = 'ambiguous:stale_lock', locked_at = NULL,
        attempted_at = reference_time
    WHERE delivery.channel = 'telegram' AND delivery.status = 'queued'
      AND (delivery.locked_at IS NULL OR delivery.locked_at < reference_time - interval '15 minutes');

    SELECT report.id INTO selected_report_id
    FROM cti.reports AS report
    LEFT JOIN cti.delivery_log AS delivery
      ON delivery.report_id = report.id AND delivery.channel = 'telegram'
    WHERE report.report_type = 'weekly' AND report.status = 'ready'
      AND (delivery.id IS NULL OR (delivery.status = 'failed' AND delivery.attempts < 3
           AND delivery.error_code LIKE 'retry_safe:%'))
    ORDER BY report.window_end, report.id LIMIT 1
    FOR UPDATE OF report SKIP LOCKED;
    IF selected_report_id IS NULL THEN RETURN; END IF;

    UPDATE cti.delivery_log AS delivery SET
        status = 'queued', attempts = delivery.attempts + 1,
        locked_at = reference_time, attempted_at = reference_time,
        error_code = NULL, external_message_id = NULL
    WHERE delivery.report_id = selected_report_id AND delivery.channel = 'telegram';
    IF NOT FOUND THEN
        INSERT INTO cti.delivery_log (report_id, channel, status, attempts, locked_at, attempted_at)
        VALUES (selected_report_id, 'telegram', 'queued', 1, reference_time, reference_time);
    END IF;

    RETURN QUERY SELECT delivery.id, report.id, report.title, report.content
    FROM cti.delivery_log AS delivery JOIN cti.reports AS report ON report.id = delivery.report_id
    WHERE report.id = selected_report_id AND delivery.channel = 'telegram'
      AND delivery.status = 'queued';
END;
$$;

CREATE OR REPLACE FUNCTION cti.complete_weekly_telegram_delivery(
    delivery_id_value bigint, external_message_ids_value text
) RETURNS void LANGUAGE plpgsql SECURITY DEFINER SET search_path = cti, pg_temp AS $$
DECLARE selected_report_id bigint; reference_time timestamptz := now(); updated_reports integer;
BEGIN
    IF external_message_ids_value IS NULL OR char_length(trim(external_message_ids_value)) NOT BETWEEN 1 AND 1000 THEN
        RAISE EXCEPTION 'Telegram delivery receipt is invalid.' USING ERRCODE = '22023';
    END IF;
    UPDATE cti.delivery_log AS delivery
    SET status = 'sent', external_message_id = trim(external_message_ids_value), error_code = NULL,
        locked_at = NULL, attempted_at = reference_time
    WHERE delivery.id = delivery_id_value AND delivery.channel = 'telegram'
      AND delivery.status = 'queued'
    RETURNING delivery.report_id INTO selected_report_id;
    IF selected_report_id IS NULL THEN
        RAISE EXCEPTION 'Telegram delivery reservation is not active.' USING ERRCODE = '55000';
    END IF;
    UPDATE cti.reports AS report SET status = 'sent', sent_at = reference_time
    WHERE report.id = selected_report_id AND report.report_type = 'weekly'
      AND report.status = 'ready';
    GET DIAGNOSTICS updated_reports = ROW_COUNT;
    IF updated_reports <> 1 THEN
        RAISE EXCEPTION 'Weekly report is not ready for Telegram completion.' USING ERRCODE = '55000';
    END IF;
END;
$$;

CREATE OR REPLACE FUNCTION cti.fail_weekly_telegram_delivery(
    delivery_id_value bigint, error_code_value text, retry_safe_value boolean DEFAULT false
) RETURNS void LANGUAGE plpgsql SECURITY DEFINER SET search_path = cti, pg_temp AS $$
DECLARE normalized_error text;
BEGIN
    normalized_error := regexp_replace(lower(coalesce(error_code_value, 'unknown')), '[^a-z0-9_.-]+', '_', 'g');
    normalized_error := left(trim(both '_' FROM normalized_error), 80);
    IF normalized_error = '' THEN normalized_error := 'unknown'; END IF;
    UPDATE cti.delivery_log AS delivery
    SET status = 'failed', error_code = (CASE WHEN retry_safe_value THEN 'retry_safe:' ELSE 'ambiguous:' END) || normalized_error,
        locked_at = NULL, attempted_at = now()
    WHERE delivery.id = delivery_id_value AND delivery.channel = 'telegram'
      AND delivery.status = 'queued';
    IF NOT FOUND THEN RAISE EXCEPTION 'Telegram delivery reservation is not active.' USING ERRCODE = '55000'; END IF;
END;
$$;

REVOKE ALL ON FUNCTION cti.claim_weekly_telegram_delivery() FROM PUBLIC;
REVOKE ALL ON FUNCTION cti.complete_weekly_telegram_delivery(bigint, text) FROM PUBLIC;
REVOKE ALL ON FUNCTION cti.fail_weekly_telegram_delivery(bigint, text, boolean) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION cti.claim_weekly_telegram_delivery() TO cti_n8n;
GRANT EXECUTE ON FUNCTION cti.complete_weekly_telegram_delivery(bigint, text) TO cti_n8n;
GRANT EXECUTE ON FUNCTION cti.fail_weekly_telegram_delivery(bigint, text, boolean) TO cti_n8n;

INSERT INTO cti.schema_versions (version) VALUES (8) ON CONFLICT (version) DO NOTHING;

COMMIT;
