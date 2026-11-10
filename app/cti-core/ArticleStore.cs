using Microsoft.Data.Sqlite;

namespace CtiCore;

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
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS articles (
                url TEXT PRIMARY KEY NOT NULL,
                source TEXT NOT NULL,
                title TEXT NOT NULL,
                published_unix INTEGER,
                first_seen_unix INTEGER NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public int Save(IReadOnlyList<Article> articles)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
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
        cmd.Parameters.AddWithValue("$seen", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        int added = 0;
        foreach (var article in articles)
        {
            url.Value = article.Url;
            source.Value = article.Source;
            title.Value = article.Title;
            published.Value = article.Published is { } date ? date.ToUnixTimeSeconds() : DBNull.Value;
            added += cmd.ExecuteNonQuery();
        }
        tx.Commit();
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
