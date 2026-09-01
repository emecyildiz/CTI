\set ON_ERROR_STOP on

UPDATE cti.sources
SET allowed_hosts = ARRAY['www.microsoft.com', 'azure.microsoft.com'],
    updated_at = now()
WHERE name = 'Microsoft Security Blog'
  AND feed_url = 'https://www.microsoft.com/en-us/security/blog/feed/';

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM cti.sources
        WHERE name = 'Microsoft Security Blog'
          AND feed_url = 'https://www.microsoft.com/en-us/security/blog/feed/'
          AND allowed_hosts = ARRAY['www.microsoft.com', 'azure.microsoft.com']
    ) THEN
        RAISE EXCEPTION 'The reviewed Microsoft Security Blog source was not found.';
    END IF;
END;
$$;

INSERT INTO cti.schema_versions (version)
VALUES (16)
ON CONFLICT (version) DO NOTHING;
