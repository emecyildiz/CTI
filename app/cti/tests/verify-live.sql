\set ON_ERROR_STOP on

DO $$
BEGIN
    IF COALESCE((SELECT max(version) FROM cti.schema_versions), 0) < 29 THEN
        RAISE EXCEPTION 'CTI schema version 29 is not installed.';
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

    IF NOT has_function_privilege(
        'cti_n8n',
        'cti.record_source_check(bigint,boolean,text)',
        'EXECUTE'
    ) THEN
        RAISE EXCEPTION 'The n8n role cannot record source health.';
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM cti.sources
        WHERE name = 'The Hacker News'
          AND feed_url = 'https://feeds.feedburner.com/TheHackersNews'
          AND allowed_hosts @> ARRAY['thehackernews.com', 'www.thehackernews.com']
    ) THEN
        RAISE EXCEPTION 'The initial CTI source is missing or invalid.';
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM cti.sources
        WHERE name = 'CISA Cybersecurity Advisories'
          AND feed_url = 'https://www.cisa.gov/cybersecurity-advisories/all.xml'
          AND allowed_hosts @> ARRAY['cisa.gov', 'www.cisa.gov']
          AND content_selector = '.l-page-section--rich-text .l-page-section__content'
          AND trust_score = 95
    ) THEN
        RAISE EXCEPTION 'The CISA CTI source is missing or invalid.';
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM cti.sources
        WHERE name = 'Microsoft Security Blog'
          AND feed_url = 'https://www.microsoft.com/en-us/security/blog/feed/'
          AND allowed_hosts = ARRAY['www.microsoft.com', 'azure.microsoft.com']
          AND content_selector = '.entry-content'
          AND trust_score = 90
    ) THEN
        RAISE EXCEPTION 'The Microsoft Security Blog CTI source is missing or invalid.';
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM cti.sources
        WHERE name = 'BleepingComputer'
          AND feed_url = 'https://www.bleepingcomputer.com/feed/'
          AND allowed_hosts = ARRAY['bleepingcomputer.com', 'www.bleepingcomputer.com']
          AND content_selector = '.articleBody'
          AND trust_score = 85
    ) THEN
        RAISE EXCEPTION 'The BleepingComputer CTI source is missing or invalid.';
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM cti.sources
        WHERE name = 'Cisco Talos'
          AND feed_url = 'https://blog.talosintelligence.com/rss/'
          AND allowed_hosts = ARRAY['blog.talosintelligence.com']
          AND content_selector = '.post-content'
          AND trust_score = 92
    ) THEN
        RAISE EXCEPTION 'The Cisco Talos CTI source is missing or invalid.';
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM cti.sources
        WHERE name = 'Krebs on Security'
          AND feed_url = 'https://krebsonsecurity.com/feed/'
          AND allowed_hosts = ARRAY['krebsonsecurity.com', 'www.krebsonsecurity.com']
          AND content_selector = '.entry-content'
          AND trust_score = 90
    ) THEN
        RAISE EXCEPTION 'The Krebs on Security CTI source is missing or invalid.';
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM cti.sources
        WHERE name = 'Dark Reading'
          AND enabled = false
          AND feed_url = 'https://www.darkreading.com/feeds/rss.xml'
    ) OR NOT EXISTS (
        SELECT 1
        FROM cti.sources
        WHERE name = 'SecurityWeek'
          AND enabled = false
          AND feed_url = 'https://www.securityweek.com/feed/'
    ) THEN
        RAISE EXCEPTION 'A reviewed but compatibility-disabled CTI source is missing.';
    END IF;

    IF (SELECT count(*) FROM cti.sources WHERE enabled) NOT BETWEEN 1 AND 6 OR
       EXISTS (
           SELECT 1
           FROM cti.sources
           WHERE enabled
             AND name NOT IN (
                 'The Hacker News',
                 'CISA Cybersecurity Advisories',
                 'Microsoft Security Blog',
                 'BleepingComputer',
                 'Cisco Talos',
                 'Krebs on Security'
             )
       ) THEN
        RAISE EXCEPTION 'The enabled reviewed-source selection is invalid.';
    END IF;

    IF NOT has_function_privilege(
        'cti_n8n',
        'cti.claim_analysis_jobs(integer,integer,integer)',
        'EXECUTE'
    ) THEN
        RAISE EXCEPTION 'The n8n role cannot claim analysis jobs.';
    END IF;

    IF NOT has_function_privilege(
        'cti_n8n',
        'cti.record_article_rule_triage(bigint,jsonb)',
        'EXECUTE'
    ) THEN
        RAISE EXCEPTION 'The n8n role cannot record rule triage.';
    END IF;

    IF NOT has_function_privilege(
        'cti_n8n',
        'cti.sync_cisa_kev_catalog(jsonb)',
        'EXECUTE'
    ) OR NOT has_function_privilege(
        'cti_n8n',
        'cti.get_epss_lookup_batch(integer)',
        'EXECUTE'
    ) OR NOT has_function_privilege(
        'cti_n8n',
        'cti.record_epss_scores(jsonb,text[])',
        'EXECUTE'
    ) THEN
        RAISE EXCEPTION 'The n8n role cannot run vulnerability enrichment.';
    END IF;

    IF to_regprocedure('cti.record_epss_scores(jsonb)') IS NOT NULL OR NOT EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_schema = 'cti'
          AND table_name = 'vulnerabilities'
          AND column_name = 'epss_last_attempted_at'
    ) THEN
        RAISE EXCEPTION 'The fair EPSS lookup schema is incomplete.';
    END IF;

    IF has_table_privilege('cti_dashboard', 'cti.articles', 'SELECT') THEN
        RAISE EXCEPTION 'The dashboard role unexpectedly reads the articles table directly.';
    END IF;

    IF has_table_privilege('cti_n8n', 'cti.events', 'SELECT') OR
       has_function_privilege(
           'cti_n8n',
           'cti.assign_exact_cve_event(bigint,text)',
           'EXECUTE'
       ) OR has_table_privilege('cti_dashboard', 'cti.events', 'SELECT') THEN
        RAISE EXCEPTION 'A restricted role unexpectedly has direct event-clustering access.';
    END IF;

    IF NOT has_table_privilege('cti_dashboard', 'cti.dashboard_articles', 'SELECT') OR
       NOT has_table_privilege('cti_dashboard', 'cti.dashboard_reports', 'SELECT') OR
       NOT has_table_privilege('cti_dashboard', 'cti.dashboard_ai_usage', 'SELECT') OR
       NOT has_table_privilege('cti_dashboard', 'cti.dashboard_system_status', 'SELECT') OR
       NOT has_table_privilege('cti_dashboard', 'cti.dashboard_ai_provider_status', 'SELECT') OR
       NOT has_table_privilege('cti_dashboard', 'cti.dashboard_source_options', 'SELECT') THEN
        RAISE EXCEPTION 'The dashboard role cannot read its restricted views.';
    END IF;

    IF has_table_privilege('cti_dashboard', 'cti.ai_provider_profile', 'SELECT') OR
       has_table_privilege('cti_dashboard', 'cti.ai_provider_profile', 'INSERT') OR
       has_table_privilege('cti_dashboard', 'cti.ai_provider_profile', 'UPDATE') OR
       has_table_privilege('cti_n8n', 'cti.ai_provider_profile', 'SELECT') OR
       has_table_privilege('cti_n8n', 'cti.ai_provider_profile', 'INSERT') OR
       has_table_privilege('cti_n8n', 'cti.ai_provider_profile', 'UPDATE') THEN
        RAISE EXCEPTION 'A restricted role can access the AI provider profile directly.';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_schema = 'cti'
          AND table_name = 'ai_provider_profile'
          AND column_name ~ '(api_key|secret|token|password|credential)'
    ) THEN
        RAISE EXCEPTION 'The AI provider profile contains a forbidden credential column.';
    END IF;

    IF (SELECT count(*) FROM cti.dashboard_ai_provider_status) <> 1 THEN
        RAISE EXCEPTION 'The dashboard AI provider status must return exactly one row.';
    END IF;

    IF NOT has_function_privilege(
        'cti_dashboard',
        'cti.configure_ai_provider_profile(text,text,text)',
        'EXECUTE'
    ) OR has_function_privilege(
        'cti_n8n',
        'cti.configure_ai_provider_profile(text,text,text)',
        'EXECUTE'
    ) THEN
        RAISE EXCEPTION 'The AI provider profile write capability is assigned incorrectly.';
    END IF;

    IF has_table_privilege('cti_dashboard', 'cti.dashboard_articles', 'UPDATE') THEN
        RAISE EXCEPTION 'The dashboard role unexpectedly has write access.';
    END IF;

    IF NOT has_function_privilege(
        'cti_dashboard',
        'cti.configure_reviewed_sources(text[])',
        'EXECUTE'
    ) OR has_function_privilege(
        'cti_n8n',
        'cti.configure_reviewed_sources(text[])',
        'EXECUTE'
    ) THEN
        RAISE EXCEPTION 'The reviewed-source write capability is assigned incorrectly.';
    END IF;
END;
$$;

SELECT
    max(version) AS schema_version,
    has_table_privilege('cti_n8n', 'cti.articles', 'DELETE') AS n8n_can_delete,
    has_function_privilege(
        'cti_n8n',
        'cti.ingest_feed_item(bigint,text,text,text,timestamp with time zone)',
        'EXECUTE'
    ) AS n8n_can_ingest,
    has_table_privilege(
        'cti_dashboard', 'cti.dashboard_articles', 'SELECT'
    ) AS dashboard_can_read,
    has_table_privilege(
        'cti_dashboard', 'cti.articles', 'SELECT'
    ) AS dashboard_can_read_base_table,
    (SELECT count(*) FROM cti.sources WHERE enabled = true) AS enabled_sources
FROM cti.schema_versions;
