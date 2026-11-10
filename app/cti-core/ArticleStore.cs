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
            Pooling = false
        }.ToString();
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS articles (
                url TEXT PRIMARY KEY NOT NULL,
                source TEXT NOT NULL,
                title TEXT NOT NULL,
                published_unix INTEGER,
                first_seen_unix INTEGER NOT NULL
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
            INSERT INTO articles (url, source, title, published_unix, first_seen_unix)
            VALUES ($url, $source, $title, $published, $seen)
            ON CONFLICT (url) DO NOTHING;
            """;
        var url = cmd.Parameters.Add("$url", SqliteType.Text);
        var source = cmd.Parameters.Add("$source", SqliteType.Text);
        var title = cmd.Parameters.Add("$title", SqliteType.Text);
        var published = cmd.Parameters.Add("$published", SqliteType.Integer);
        cmd.Parameters.AddWithValue("$seen", finished.ToUnixTimeSeconds());
        int added = 0;
        foreach (var article in articles)
        {
            url.Value = article.Url;
            source.Value = article.Source;
            title.Value = article.Title;
            published.Value = article.Published is { } date ? date.ToUnixTimeSeconds() : DBNull.Value;
            added += cmd.ExecuteNonQuery();
        }
        return added;
    }

    public IReadOnlyList<Article> Latest()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT url, source, title, published_unix FROM articles
            ORDER BY coalesce(published_unix, first_seen_unix) DESC, url LIMIT 100;
            """;
        using var rows = cmd.ExecuteReader();
        var result = new List<Article>();
        while (rows.Read())
            result.Add(new Article(rows.GetString(0), rows.GetString(1), rows.GetString(2),
                rows.IsDBNull(3) ? null : DateTimeOffset.FromUnixTimeSeconds(rows.GetInt64(3))));
        return result;
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
