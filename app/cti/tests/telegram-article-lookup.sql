BEGIN;

DO $$
DECLARE
    source_id_value bigint;
    malware_article_id bigint;
    vulnerability_article_id bigint;
    old_article_id bigint;
    category_result jsonb;
    search_result jsonb;
    menu_result jsonb;
BEGIN
    INSERT INTO cti.sources (name, feed_url, allowed_hosts, trust_score, enabled)
    VALUES ('Telegram lookup test', 'https://lookup.example.test/feed.xml', ARRAY['lookup.example.test'], 80, true)
    RETURNING id INTO source_id_value;

    INSERT INTO cti.articles (
        canonical_url, title, normalized_title, title_hash, published_at,
        category, severity, summary_tr, analyzed_at
    ) VALUES ('https://lookup.example.test/malware', 'Ransomware campaign update',
       'ransomware campaign update', repeat('a', 64), now() - interval '1 day', 'malware', 'high',
       'Test summary about a ransomware campaign with reviewed facts.', now())
    RETURNING id INTO malware_article_id;
    INSERT INTO cti.articles (
        canonical_url, title, normalized_title, title_hash, published_at,
        category, severity, summary_tr, analyzed_at
    ) VALUES ('https://lookup.example.test/vulnerability', 'Critical browser vulnerability',
       'critical browser vulnerability', repeat('b', 64), now() - interval '2 days', 'vulnerability', 'critical',
       'Test summary about a browser vulnerability and its affected component.', now())
    RETURNING id INTO vulnerability_article_id;
    INSERT INTO cti.articles (
        canonical_url, title, normalized_title, title_hash, published_at,
        category, severity, summary_tr, analyzed_at
    ) VALUES ('https://lookup.example.test/old', 'Old malware report',
       'old malware report', repeat('c', 64), now() - interval '9 days', 'malware', 'medium',
       'This older result must remain outside the Telegram lookup window.', now())
    RETURNING id INTO old_article_id;

    INSERT INTO cti.article_occurrences (
        article_id, source_id, original_url, source_title, source_published_at
    ) VALUES
      (malware_article_id, source_id_value, 'https://lookup.example.test/malware',
       'Ransomware campaign update', now() - interval '1 day'),
      (vulnerability_article_id, source_id_value, 'https://lookup.example.test/vulnerability',
       'Critical browser vulnerability', now() - interval '2 days'),
      (old_article_id, source_id_value, 'https://lookup.example.test/old',
       'Old malware report', now() - interval '9 days');

    category_result := cti.telegram_article_lookup('category', 'malware', 5);
    IF jsonb_array_length(category_result->'articles') <> 1
       OR category_result#>>'{articles,0,category}' <> 'malware' THEN
        RAISE EXCEPTION 'Category lookup did not enforce the recent window.';
    END IF;

    search_result := cti.telegram_article_lookup('search', 'browser vulnerability', 5);
    IF jsonb_array_length(search_result->'articles') <> 1
       OR search_result#>>'{articles,0,severity}' <> 'critical' THEN
        RAISE EXCEPTION 'Keyword lookup did not return the expected article.';
    END IF;

    menu_result := cti.telegram_article_lookup('menu', NULL, 5);
    IF jsonb_array_length(menu_result->'articles') <> 0 THEN
        RAISE EXCEPTION 'Menu lookup unexpectedly returned articles.';
    END IF;

    BEGIN
        PERFORM cti.telegram_article_lookup('search', '%', 5);
        RAISE EXCEPTION 'Unsafe search query was accepted.';
    EXCEPTION WHEN SQLSTATE '22023' THEN NULL;
    END;
END;
$$;

SELECT 'CTI Telegram article lookup tests passed.' AS result;
ROLLBACK;
