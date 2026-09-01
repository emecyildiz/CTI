BEGIN;

DO $$
DECLARE
    first_report_id bigint;
    second_report_id bigint;
    claimed_delivery_id bigint;
    claimed_report_id bigint;
BEGIN
    INSERT INTO cti.reports (report_type, window_start, window_end, status, title, content)
    VALUES ('weekly', '2098-01-01T00:00:00Z', '2098-01-08T00:00:00Z', 'ready',
            'Telegram delivery test', 'A deterministic weekly report body.')
    RETURNING id INTO first_report_id;

    SELECT delivery_id, report_id INTO claimed_delivery_id, claimed_report_id
    FROM cti.claim_weekly_telegram_delivery();
    IF claimed_report_id IS DISTINCT FROM first_report_id THEN
        RAISE EXCEPTION 'Ready weekly report was not claimed.';
    END IF;

    PERFORM cti.fail_weekly_telegram_delivery(claimed_delivery_id, 'network_before_send', true);
    SELECT delivery_id, report_id INTO claimed_delivery_id, claimed_report_id
    FROM cti.claim_weekly_telegram_delivery();
    IF claimed_report_id IS DISTINCT FROM first_report_id THEN
        RAISE EXCEPTION 'Retry-safe delivery was not reclaimed.';
    END IF;

    PERFORM cti.complete_weekly_telegram_delivery(claimed_delivery_id, '1001,1002');
    IF NOT EXISTS (SELECT 1 FROM cti.reports WHERE id = first_report_id AND status = 'sent' AND sent_at IS NOT NULL)
       OR NOT EXISTS (SELECT 1 FROM cti.delivery_log WHERE id = claimed_delivery_id AND status = 'sent' AND attempts = 2) THEN
        RAISE EXCEPTION 'Completed Telegram delivery state is invalid.';
    END IF;

    INSERT INTO cti.reports (report_type, window_start, window_end, status, title, content)
    VALUES ('weekly', '2098-01-08T00:00:00Z', '2098-01-15T00:00:00Z', 'ready',
            'Ambiguous delivery test', 'A second deterministic weekly report body.')
    RETURNING id INTO second_report_id;
    SELECT delivery_id, report_id INTO claimed_delivery_id, claimed_report_id
    FROM cti.claim_weekly_telegram_delivery();
    PERFORM cti.fail_weekly_telegram_delivery(claimed_delivery_id, 'unknown_transport_state', false);

    claimed_report_id := NULL;
    SELECT report_id INTO claimed_report_id FROM cti.claim_weekly_telegram_delivery();
    IF claimed_report_id IS NOT NULL THEN
        RAISE EXCEPTION 'Ambiguous delivery was automatically retried.';
    END IF;
END;
$$;

SELECT 'CTI weekly Telegram delivery tests passed.' AS result;
ROLLBACK;
