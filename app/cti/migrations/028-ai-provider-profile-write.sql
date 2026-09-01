\set ON_ERROR_STOP on

CREATE OR REPLACE FUNCTION cti.configure_ai_provider_profile(
    provider_type_value text,
    model_identifier_value text,
    api_base_url_value text DEFAULT NULL
)
RETURNS void
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, cti
AS $$
DECLARE
    normalized_provider text := lower(btrim(coalesce(provider_type_value, '')));
    normalized_model text := btrim(coalesce(model_identifier_value, ''));
    normalized_url text := nullif(btrim(coalesce(api_base_url_value, '')), '');
    selected_label text;
    selected_adapter text;
BEGIN
    CASE normalized_provider
        WHEN 'google_gemini' THEN
            selected_label := 'Google Gemini';
            selected_adapter := 'google_gemini';
        WHEN 'openai_compatible' THEN
            selected_label := 'OpenAI-compatible';
            selected_adapter := 'openai_compatible';
        WHEN 'custom' THEN
            selected_label := 'Custom provider';
            selected_adapter := 'custom';
        ELSE
            RAISE EXCEPTION 'Unsupported AI provider type.'
                USING ERRCODE = '22023';
    END CASE;

    IF char_length(normalized_model) NOT BETWEEN 1 AND 200
       OR normalized_model ~ '[[:cntrl:]]' THEN
        RAISE EXCEPTION 'Invalid AI model identifier.'
            USING ERRCODE = '22023';
    END IF;

    IF normalized_url IS NOT NULL AND (
        char_length(normalized_url) NOT BETWEEN 8 AND 500
        OR normalized_url !~ '^https?://[^[:space:]]+$'
        OR normalized_url ~ '[[:cntrl:]@?#]'
    ) THEN
        RAISE EXCEPTION 'Invalid AI API base URL.'
            USING ERRCODE = '22023';
    END IF;

    INSERT INTO cti.ai_provider_profile (
        profile_id,
        provider_key,
        provider_label,
        adapter_key,
        model_identifier,
        api_base_url,
        updated_at
    )
    VALUES (
        1,
        normalized_provider,
        selected_label,
        selected_adapter,
        normalized_model,
        normalized_url,
        now()
    )
    ON CONFLICT (profile_id) DO UPDATE
    SET provider_key = EXCLUDED.provider_key,
        provider_label = EXCLUDED.provider_label,
        adapter_key = EXCLUDED.adapter_key,
        model_identifier = EXCLUDED.model_identifier,
        api_base_url = EXCLUDED.api_base_url,
        updated_at = now();
END;
$$;

REVOKE ALL ON FUNCTION cti.configure_ai_provider_profile(text, text, text)
    FROM PUBLIC, cti_n8n;
GRANT EXECUTE ON FUNCTION cti.configure_ai_provider_profile(text, text, text)
    TO cti_dashboard;

INSERT INTO cti.schema_versions (version)
VALUES (28)
ON CONFLICT (version) DO NOTHING;
