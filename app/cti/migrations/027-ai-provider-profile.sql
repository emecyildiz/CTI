\set ON_ERROR_STOP on

-- This table is intentionally non-secret. API credentials belong in the
-- operator-selected credential backend (n8n in the bundled deployment).
CREATE TABLE IF NOT EXISTS cti.ai_provider_profile (
    profile_id smallint PRIMARY KEY DEFAULT 1 CHECK (profile_id = 1),
    provider_key text NOT NULL CHECK (
        char_length(provider_key) BETWEEN 2 AND 50
        AND provider_key ~ '^[a-z0-9][a-z0-9_-]*$'
    ),
    provider_label text NOT NULL CHECK (
        char_length(provider_label) BETWEEN 2 AND 80
        AND provider_label !~ '[[:cntrl:]]'
    ),
    adapter_key text NOT NULL CHECK (
        char_length(adapter_key) BETWEEN 2 AND 50
        AND adapter_key ~ '^[a-z0-9][a-z0-9_-]*$'
    ),
    model_identifier text NOT NULL CHECK (
        char_length(model_identifier) BETWEEN 1 AND 200
        AND model_identifier !~ '[[:cntrl:]]'
    ),
    api_base_url text CHECK (
        api_base_url IS NULL
        OR (
            char_length(api_base_url) BETWEEN 8 AND 500
            AND api_base_url ~ '^https?://[^[:space:]]+$'
            AND api_base_url !~ '[[:cntrl:]]'
            AND api_base_url !~ '[@?#]'
        )
    ),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now()
);

COMMENT ON TABLE cti.ai_provider_profile IS
    'Non-secret AI provider metadata. API credentials must not be stored here.';

REVOKE ALL ON cti.ai_provider_profile FROM PUBLIC, cti_n8n, cti_dashboard;

CREATE OR REPLACE VIEW cti.dashboard_ai_provider_status
WITH (security_barrier = true)
AS
SELECT
    profile.profile_id IS NOT NULL AS profile_defined,
    profile.provider_key,
    profile.provider_label,
    profile.adapter_key,
    profile.model_identifier,
    profile.api_base_url,
    CASE
        WHEN profile.profile_id IS NULL THEN 'not_configured'
        WHEN profile.adapter_key = 'google_gemini' THEN 'bundled'
        ELSE 'manual_adapter_required'
    END AS adapter_status,
    profile.updated_at
FROM (VALUES (1::smallint)) AS slot(profile_id)
LEFT JOIN cti.ai_provider_profile AS profile
    ON profile.profile_id = slot.profile_id;

REVOKE ALL ON cti.dashboard_ai_provider_status FROM PUBLIC;
GRANT SELECT ON cti.dashboard_ai_provider_status TO cti_dashboard;

INSERT INTO cti.schema_versions (version)
VALUES (27)
ON CONFLICT (version) DO NOTHING;
