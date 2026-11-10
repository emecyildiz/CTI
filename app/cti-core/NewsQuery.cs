using System.Globalization;
using Microsoft.Extensions.Primitives;

namespace CtiCore;

internal sealed record NewsQuery(FeedSource? Source, string Title, int Page)
{
    public const int PageSize = 100;
    public const int MaxPage = 1000;
    public const int MaxTitleLength = 120;

    public static bool TryRead(StringValues filters, StringValues titles, StringValues pages, out NewsQuery query)
    {
        query = null!;
        if (filters.Count > 1 || titles.Count > 1 || pages.Count > 1) return false;
        var source = filters.Count == 1 ? FeedSource.Find(filters[0]) : null;
        if (filters.Count == 1 && source is null) return false;

        string title = titles.ToString();
        if (title.Length > MaxTitleLength || title.Any(char.IsControl)) return false;
        int page = 1;
        if (pages.Count == 1 && (!int.TryParse(pages[0], NumberStyles.None, CultureInfo.InvariantCulture, out page)
            || page is < 1 or > MaxPage))
            return false;

        query = new(source, title.Trim(), page);
        return true;
    }

    public IEnumerable<KeyValuePair<string, string?>> Parameters()
    {
        if (Source is not null) yield return new("filter", Source.Id);
        if (Title.Length != 0) yield return new("q", Title);
        if (Page > 1) yield return new("page", Page.ToString(CultureInfo.InvariantCulture));
    }
}

internal sealed record NewsPage(IReadOnlyList<Article> Articles, bool HasNext);
