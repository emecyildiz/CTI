\set ON_ERROR_STOP on

CREATE OR REPLACE VIEW cti.dashboard_source_options
WITH (security_barrier = true)
AS
SELECT
    source.name,
    source.trust_score,
    source.enabled,
    source.name IN (
        'The Hacker News',
        'CISA Cybersecurity Advisories',
        'Microsoft Security Blog',
        'BleepingComputer',
        'Cisco Talos',
        'Krebs on Security'
    ) AS selectable,
    source.last_checked_at,
    source.last_success_at,
    source.last_error_code
FROM cti.sources AS source
WHERE source.name IN (
    'The Hacker News',
    'CISA Cybersecurity Advisories',
    'Microsoft Security Blog',
    'BleepingComputer',
    'Cisco Talos',
    'Krebs on Security',
    'Dark Reading',
    'SecurityWeek'
);

CREATE OR REPLACE FUNCTION cti.configure_reviewed_sources(
    enabled_source_names text[]
)
RETURNS void
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, cti
AS $$
DECLARE
    normalized_names text[];
    supplied_count integer;
BEGIN
    supplied_count := cardinality(enabled_source_names);
    IF enabled_source_names IS NULL OR supplied_count NOT BETWEEN 1 AND 6 THEN
        RAISE EXCEPTION 'Select between one and six reviewed sources.'
            USING ERRCODE = '22023';
    END IF;

    SELECT array_agg(DISTINCT btrim(source_name) ORDER BY btrim(source_name))
    INTO normalized_names
    FROM unnest(enabled_source_names) AS selected(source_name);

    IF cardinality(normalized_names) <> supplied_count OR
       EXISTS (
           SELECT 1
           FROM unnest(normalized_names) AS selected(source_name)
           WHERE selected.source_name IS NULL
              OR selected.source_name NOT IN (
               'The Hacker News',
               'CISA Cybersecurity Advisories',
               'Microsoft Security Blog',
               'BleepingComputer',
               'Cisco Talos',
               'Krebs on Security'
           )
       ) THEN
        RAISE EXCEPTION 'The source selection contains an unknown, unavailable, or duplicate source.'
            USING ERRCODE = '22023';
    END IF;

    IF (
        SELECT count(*)
        FROM cti.sources AS source
        WHERE source.name IN (
            'The Hacker News',
            'CISA Cybersecurity Advisories',
            'Microsoft Security Blog',
            'BleepingComputer',
            'Cisco Talos',
            'Krebs on Security',
            'Dark Reading',
            'SecurityWeek'
        )
    ) <> 8 THEN
        RAISE EXCEPTION 'The reviewed source catalog is incomplete.';
    END IF;

    UPDATE cti.sources AS source
    SET enabled = source.name = ANY(normalized_names),
        updated_at = clock_timestamp()
    WHERE source.name IN (
        'The Hacker News',
        'CISA Cybersecurity Advisories',
        'Microsoft Security Blog',
        'BleepingComputer',
        'Cisco Talos',
        'Krebs on Security',
        'Dark Reading',
        'SecurityWeek'
    )
      AND source.enabled IS DISTINCT FROM (source.name = ANY(normalized_names));
END;
$$;

REVOKE ALL ON cti.dashboard_source_options FROM PUBLIC;
REVOKE ALL ON FUNCTION cti.configure_reviewed_sources(text[])
    FROM PUBLIC, cti_n8n;
GRANT SELECT ON cti.dashboard_source_options TO cti_dashboard;
GRANT EXECUTE ON FUNCTION cti.configure_reviewed_sources(text[])
    TO cti_dashboard;

INSERT INTO cti.schema_versions (version)
VALUES (29)
ON CONFLICT (version) DO NOTHING;
