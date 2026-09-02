\set ON_ERROR_STOP on

BEGIN;

DO $$
DECLARE
    rejected boolean;
BEGIN
    IF NOT has_function_privilege(
        'cti_dashboard',
        'cti.configure_reviewed_sources(text[])',
        'EXECUTE'
    ) OR has_function_privilege(
        'cti_n8n',
        'cti.configure_reviewed_sources(text[])',
        'EXECUTE'
    ) THEN
        RAISE EXCEPTION 'Reviewed-source write capability is assigned incorrectly.';
    END IF;

    IF NOT has_table_privilege(
        'cti_dashboard',
        'cti.dashboard_source_options',
        'SELECT'
    ) OR has_table_privilege('cti_dashboard', 'cti.sources', 'SELECT') THEN
        RAISE EXCEPTION 'Reviewed-source read capability is assigned incorrectly.';
    END IF;

    PERFORM cti.configure_reviewed_sources(ARRAY[
        'CISA Cybersecurity Advisories',
        'Cisco Talos'
    ]);

    IF (SELECT count(*) FROM cti.sources WHERE enabled) <> 2 OR
       NOT EXISTS (
           SELECT 1 FROM cti.sources
           WHERE name = 'CISA Cybersecurity Advisories' AND enabled
       ) OR NOT EXISTS (
           SELECT 1 FROM cti.sources
           WHERE name = 'Cisco Talos' AND enabled
       ) OR EXISTS (
           SELECT 1 FROM cti.sources
           WHERE name IN ('Dark Reading', 'SecurityWeek') AND enabled
       ) THEN
        RAISE EXCEPTION 'Reviewed-source selection was not applied atomically.';
    END IF;

    rejected := false;
    BEGIN
        PERFORM cti.configure_reviewed_sources(ARRAY['Unknown Source']);
    EXCEPTION WHEN invalid_parameter_value THEN
        rejected := true;
    END;
    IF NOT rejected THEN
        RAISE EXCEPTION 'An unknown source was accepted.';
    END IF;

    rejected := false;
    BEGIN
        PERFORM cti.configure_reviewed_sources(ARRAY[
            'The Hacker News',
            'The Hacker News'
        ]);
    EXCEPTION WHEN invalid_parameter_value THEN
        rejected := true;
    END;
    IF NOT rejected THEN
        RAISE EXCEPTION 'A duplicate source selection was accepted.';
    END IF;

    rejected := false;
    BEGIN
        PERFORM cti.configure_reviewed_sources(ARRAY[]::text[]);
    EXCEPTION WHEN invalid_parameter_value THEN
        rejected := true;
    END;
    IF NOT rejected THEN
        RAISE EXCEPTION 'An empty source selection was accepted.';
    END IF;

    rejected := false;
    BEGIN
        PERFORM cti.configure_reviewed_sources(ARRAY[
            'The Hacker News',
            NULL
        ]::text[]);
    EXCEPTION WHEN invalid_parameter_value THEN
        rejected := true;
    END;
    IF NOT rejected THEN
        RAISE EXCEPTION 'A null source selection was accepted.';
    END IF;
END;
$$;

ROLLBACK;
