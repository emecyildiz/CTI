BEGIN;

CREATE INDEX IF NOT EXISTS ix_articles_simple_search
    ON cti.articles USING gin (
        to_tsvector('simple', title || ' ' || coalesce(summary_tr, ''))
    );

CREATE OR REPLACE FUNCTION cti.telegram_article_lookup(
    action_value text,
    query_value text DEFAULT NULL,
    result_limit_value integer DEFAULT 5
)
RETURNS jsonb
LANGUAGE plpgsql
STABLE
SECURITY DEFINER
SET search_path = cti, pg_temp
AS $$
DECLARE
    normalized_action text := lower(trim(coalesce(action_value, '')));
    normalized_query text := lower(trim(coalesce(query_value, '')));
    result_limit integer := least(greatest(coalesce(result_limit_value, 5), 1), 5);
    article_results jsonb := '[]'::jsonb;
BEGIN
    IF normalized_action NOT IN ('menu', 'help', 'category', 'search') THEN
        RAISE EXCEPTION 'Unsupported Telegram CTI action.' USING ERRCODE = '22023';
    END IF;
    IF normalized_action = 'category'
       AND normalized_query NOT IN ('malware', 'vulnerability', 'data_breach', 'threat_intelligence', 'other') THEN
        RAISE EXCEPTION 'Unsupported Telegram CTI category.' USING ERRCODE = '22023';
    END IF;
    IF normalized_action = 'search' AND (
        char_length(normalized_query) NOT BETWEEN 2 AND 60
        OR normalized_query !~ '^[[:alnum:][:space:]._:/-]+$'
    ) THEN
        RAISE EXCEPTION 'Telegram CTI search query is invalid.' USING ERRCODE = '22023';
    END IF;

    IF normalized_action IN ('category', 'search') THEN
        SELECT coalesce(jsonb_agg(to_jsonb(selected_article) ORDER BY selected_article.rank_order), '[]'::jsonb)
        INTO article_results
        FROM (
            SELECT article.id AS article_id, left(article.title, 220) AS title,
                left(article.summary_tr, 700) AS summary_tr, article.category, article.severity,
                article.canonical_url, source.name AS source_name, article.published_at,
                row_number() OVER (ORDER BY
                    CASE article.severity WHEN 'critical' THEN 1 WHEN 'high' THEN 2
                        WHEN 'medium' THEN 3 WHEN 'low' THEN 4 ELSE 5 END,
                    article.published_at DESC, article.id DESC) AS rank_order
            FROM cti.articles AS article
            JOIN LATERAL (
                SELECT source.name
                FROM cti.article_occurrences AS occurrence
                JOIN cti.sources AS source ON source.id = occurrence.source_id
                WHERE occurrence.article_id = article.id
                ORDER BY occurrence.discovered_at, occurrence.id
                LIMIT 1
            ) AS source ON true
            WHERE article.analyzed_at IS NOT NULL AND article.summary_tr IS NOT NULL
              AND article.published_at >= now() - interval '8 days'
              AND ((normalized_action = 'category' AND article.category = normalized_query)
                OR (normalized_action = 'search'
                  AND to_tsvector('simple', article.title || ' ' || coalesce(article.summary_tr, ''))
                      @@ websearch_to_tsquery('simple', normalized_query)))
            ORDER BY CASE article.severity WHEN 'critical' THEN 1 WHEN 'high' THEN 2
                    WHEN 'medium' THEN 3 WHEN 'low' THEN 4 ELSE 5 END,
                article.published_at DESC, article.id DESC
            LIMIT result_limit
        ) AS selected_article;
    END IF;

    RETURN jsonb_build_object('action', normalized_action, 'query', nullif(normalized_query, ''),
        'articles', article_results, 'generated_at', now());
END;
$$;

REVOKE ALL ON FUNCTION cti.telegram_article_lookup(text, text, integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION cti.telegram_article_lookup(text, text, integer) TO cti_n8n;

INSERT INTO cti.schema_versions (version) VALUES (9) ON CONFLICT (version) DO NOTHING;

COMMIT;
