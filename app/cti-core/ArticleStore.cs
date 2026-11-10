using System.Globalization;
using Microsoft.Data.Sqlite;

namespace CtiCore;

internal sealed record SourceState(bool Enabled, bool Automatic, long Revision, DateTimeOffset? LastAttempt,
    DateTimeOffset? LastSuccess, bool? LastAttemptSucceeded, int? LastAdded);

internal sealed class ArticleStore
{
    private readonly string connectionString;

    public ArticleStore(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(dataDirectory, "articles.sqlite3"),
            DefaultTimeout = 3,
            ForeignKeys = true,
            Pooling = false
        }.ToString();
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            CREATE TABLE IF NOT EXISTS articles (
                url TEXT PRIMARY KEY NOT NULL,
                source TEXT NOT NULL,
                title TEXT NOT NULL,
                published_unix INTEGER,
                first_seen_unix INTEGER NOT NULL,
                excerpt TEXT CHECK (excerpt IS NULL OR length(excerpt) <= {FeedExcerpt.MaximumLength})
            );
            CREATE INDEX IF NOT EXISTS ix_articles_newest
                ON articles (coalesce(published_unix, first_seen_unix) DESC, url);
            CREATE INDEX IF NOT EXISTS ix_articles_source_newest
                ON articles (source, coalesce(published_unix, first_seen_unix) DESC, url);
            CREATE TABLE IF NOT EXISTS article_summaries (
                url TEXT PRIMARY KEY NOT NULL REFERENCES articles(url) ON DELETE CASCADE,
                summary TEXT NOT NULL CHECK (length(summary) BETWEEN 1 AND {GeminiSummaryClient.MaximumSummaryLength}),
                model TEXT NOT NULL,
                created_unix INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS source_state (
                source_id TEXT PRIMARY KEY NOT NULL,
                enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
                automatic INTEGER NOT NULL CHECK (automatic IN (0, 1) AND automatic <= enabled),
                revision INTEGER NOT NULL DEFAULT 0,
                last_attempt INTEGER,
                last_success INTEGER,
                last_succeeded INTEGER CHECK (last_succeeded IN (0, 1)),
                last_added INTEGER
            );
            """;
        cmd.ExecuteNonQuery();
        cmd.CommandText = "SELECT 1 FROM pragma_table_info('articles') WHERE name = 'excerpt';";
        if (cmd.ExecuteScalar() is null)
        {
            cmd.CommandText = $"ALTER TABLE articles ADD COLUMN excerpt TEXT CHECK (excerpt IS NULL OR length(excerpt) <= {FeedExcerpt.MaximumLength});";
            cmd.ExecuteNonQuery();
        }
        cmd.CommandText = """
            INSERT INTO source_state (source_id, enabled, automatic) VALUES ($source, $enabled, 0)
            ON CONFLICT (source_id) DO NOTHING;
            """;
        var sourceId = cmd.Parameters.Add("$source", SqliteType.Text);
        var enabled = cmd.Parameters.Add("$enabled", SqliteType.Integer);
        foreach (var source in FeedSource.Catalog)
        {
            sourceId.Value = source.Id;
            enabled.Value = source.EnabledByDefault;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public SourceState ReadSource(FeedSource source)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT enabled, automatic, revision, last_attempt, last_success, last_succeeded, last_added
            FROM source_state WHERE source_id = $source;
            """;
        cmd.Parameters.AddWithValue("$source", source.Id);
        using var row = cmd.ExecuteReader();
        if (!row.Read()) throw new InvalidOperationException("The reviewed source has no settings row.");
        DateTimeOffset? Time(int column) => row.IsDBNull(column) ? null : DateTimeOffset.FromUnixTimeSeconds(row.GetInt64(column));
        return new(row.GetBoolean(0), row.GetBoolean(1), row.GetInt64(2), Time(3), Time(4),
            row.IsDBNull(5) ? null : row.GetBoolean(5), row.IsDBNull(6) ? null : row.GetInt32(6));
    }

    public void SetCollectionPolicy(FeedSource source, bool enabled, bool automatic)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            UPDATE source_state SET enabled = $enabled, automatic = $automatic, revision = revision + 1
            WHERE source_id = $source AND (enabled <> $enabled OR automatic <> $automatic);
            """;
        cmd.Parameters.AddWithValue("$source", source.Id);
        cmd.Parameters.AddWithValue("$enabled", enabled);
        cmd.Parameters.AddWithValue("$automatic", enabled && automatic);
        cmd.ExecuteNonQuery();
    }

    // null articles means a failed fetch. null return means settings changed and the fetch was discarded.
    public int? RecordCollection(FeedSource source, long revision, bool automatic,
        DateTimeOffset finished, IReadOnlyList<Article>? articles)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var update = db.CreateCommand();
        update.Transaction = tx;
        update.CommandText = """
            UPDATE source_state SET
                last_attempt = $when,
                last_success = CASE WHEN $success THEN $when ELSE last_success END,
                last_succeeded = $success,
                last_added = NULL
            WHERE source_id = $source AND enabled = 1 AND revision = $revision
                AND ($automatic = 0 OR automatic = 1);
            """;
        update.Parameters.AddWithValue("$source", source.Id);
        update.Parameters.AddWithValue("$revision", revision);
        update.Parameters.AddWithValue("$automatic", automatic);
        update.Parameters.AddWithValue("$when", finished.ToUnixTimeSeconds());
        update.Parameters.AddWithValue("$success", articles is not null);
        if (update.ExecuteNonQuery() != 1) return null;

        int added = articles is null ? 0 : InsertArticles(db, tx, articles, finished);
        if (articles is not null)
        {
            update.Parameters.Clear();
            update.CommandText = "UPDATE source_state SET last_added = $added WHERE source_id = $source;";
            update.Parameters.AddWithValue("$source", source.Id);
            update.Parameters.AddWithValue("$added", added);
            update.ExecuteNonQuery();
        }
        tx.Commit();
        return added;
    }

    private static int InsertArticles(SqliteConnection db, SqliteTransaction tx, IReadOnlyList<Article> articles, DateTimeOffset finished)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO articles (url, source, title, published_unix, first_seen_unix, excerpt)
            VALUES ($url, $source, $title, $published, $seen, $excerpt)
            ON CONFLICT (url) DO NOTHING;
            """;
        var url = cmd.Parameters.Add("$url", SqliteType.Text);
        var source = cmd.Parameters.Add("$source", SqliteType.Text);
        var title = cmd.Parameters.Add("$title", SqliteType.Text);
        var published = cmd.Parameters.Add("$published", SqliteType.Integer);
        var excerpt = cmd.Parameters.Add("$excerpt", SqliteType.Text);
        cmd.Parameters.AddWithValue("$seen", finished.ToUnixTimeSeconds());
        using var fill = db.CreateCommand();
        fill.Transaction = tx;
        fill.CommandText = """
            UPDATE articles SET excerpt = $excerpt
            WHERE url = $url AND source = $source AND excerpt IS NULL;
            """;
        var fillUrl = fill.Parameters.Add("$url", SqliteType.Text);
        var fillSource = fill.Parameters.Add("$source", SqliteType.Text);
        var fillExcerpt = fill.Parameters.Add("$excerpt", SqliteType.Text);
        int added = 0;
        foreach (var article in articles)
        {
            url.Value = article.Url;
            source.Value = article.Source;
            title.Value = article.Title;
            published.Value = article.Published is { } date ? date.ToUnixTimeSeconds() : DBNull.Value;
            excerpt.Value = article.Excerpt is { Length: > 0 } text ? text : DBNull.Value;
            int inserted = cmd.ExecuteNonQuery();
            added += inserted;
            if (inserted == 0 && excerpt.Value is string missingText)
            {
                fillUrl.Value = article.Url;
                fillSource.Value = article.Source;
                fillExcerpt.Value = missingText;
                fill.ExecuteNonQuery();
            }
        }
        return added;
    }

    public NewsPage ReadNews(NewsQuery query)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        const string select = """
            SELECT a.url, a.source, a.title, a.published_unix, a.excerpt, s.summary FROM articles a
            LEFT JOIN article_summaries s ON s.url = a.url
            """;
        const string newest = " ORDER BY coalesce(a.published_unix, a.first_seen_unix) DESC, a.url LIMIT $take OFFSET $skip;";
        var where = new List<string>(2);
        // Earlier previews already use the catalog's exact display name as the stored source.
        if (query.Source is { } source)
        {
            where.Add("a.source = $source");
            cmd.Parameters.AddWithValue("$source", source.Name);
        }
        if (query.Title.Length != 0)
        {
            // Use a literal substring rather than SQL LIKE patterns (% and _ are normal characters).
            db.CreateFunction<string, string, long>("cti_title_contains", static (title, needle) =>
                CultureInfo.InvariantCulture.CompareInfo.IndexOf(title, needle, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace)
                    >= 0 ? 1L : 0L, isDeterministic: true);
            where.Add("cti_title_contains(a.title, $title) = 1");
            cmd.Parameters.AddWithValue("$title", query.Title);
        }
        cmd.CommandText = select + (where.Count == 0 ? "" : " WHERE " + string.Join(" AND ", where)) + newest;
        cmd.Parameters.AddWithValue("$take", NewsQuery.PageSize + 1);
        cmd.Parameters.AddWithValue("$skip", (query.Page - 1) * NewsQuery.PageSize);
        using var rows = cmd.ExecuteReader();
        var result = new List<Article>();
        while (rows.Read())
            result.Add(new Article(rows.GetString(0), rows.GetString(1), rows.GetString(2),
                rows.IsDBNull(3) ? null : DateTimeOffset.FromUnixTimeSeconds(rows.GetInt64(3)),
                rows.IsDBNull(4) ? null : rows.GetString(4),
                rows.IsDBNull(5) ? null : rows.GetString(5)));
        bool extra = result.Count > NewsQuery.PageSize;
        if (extra) result.RemoveAt(result.Count - 1);
        return new(result, extra && query.Page < NewsQuery.MaxPage);
    }

    public IReadOnlyList<Article> Latest(FeedSource? filter = null) => ReadNews(new(filter, "", 1)).Articles;

    public SummaryInput? ReadSummaryInput(string url)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT a.title, a.excerpt, s.url IS NOT NULL
            FROM articles a LEFT JOIN article_summaries s ON s.url = a.url WHERE a.url = $url;
            """;
        cmd.Parameters.AddWithValue("$url", url);
        using var row = cmd.ExecuteReader();
        return row.Read() ? new(row.GetString(0), row.IsDBNull(1) ? null : row.GetString(1), row.GetBoolean(2)) : null;
    }

    public void SaveSummary(string url, string summary, string model, DateTimeOffset created)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO article_summaries (url, summary, model, created_unix)
            VALUES ($url, $summary, $model, $created) ON CONFLICT (url) DO NOTHING;
            """;
        cmd.Parameters.AddWithValue("$url", url);
        cmd.Parameters.AddWithValue("$summary", summary);
        cmd.Parameters.AddWithValue("$model", model);
        cmd.Parameters.AddWithValue("$created", created.ToUnixTimeSeconds());
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(connectionString);
        try
        {
            db.Open();
            return db;
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }
}
