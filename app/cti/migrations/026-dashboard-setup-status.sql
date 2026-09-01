\set ON_ERROR_STOP on

CREATE OR REPLACE VIEW cti.dashboard_system_status
WITH (security_barrier = true)
AS
SELECT
    COALESCE((SELECT max(version) FROM cti.schema_versions), 0)::integer AS schema_version,
    count(*) FILTER (WHERE source.enabled)::bigint AS enabled_source_count,
    count(*)::bigint AS total_source_count,
    count(*) FILTER (
        WHERE source.enabled
          AND source.last_success_at IS NOT NULL
    )::bigint AS checked_source_count,
    count(*) FILTER (
        WHERE source.enabled
          AND source.last_error_at IS NOT NULL
          AND (
              source.last_success_at IS NULL
              OR source.last_error_at > source.last_success_at
          )
    )::bigint AS failing_source_count,
    max(source.last_success_at) FILTER (WHERE source.enabled) AS last_source_success_at
FROM cti.sources AS source;

REVOKE ALL ON cti.dashboard_system_status FROM PUBLIC;
GRANT SELECT ON cti.dashboard_system_status TO cti_dashboard;

INSERT INTO cti.schema_versions (version)
VALUES (26)
ON CONFLICT (version) DO NOTHING;
