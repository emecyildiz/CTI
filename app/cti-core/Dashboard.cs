using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Antiforgery;

namespace CtiCore;

internal static class Dashboard
{
    private static readonly string Styles = LoadStyles();

    internal static string Render(NewsPage news, IReadOnlyList<(FeedSource Source, SourceState State)> sources,
        AntiforgeryTokenSet token, FeedSource? notice, string? message, NewsQuery query, bool aiEnabled, string? summaryMessage,
        string view, AppearanceSettings appearance, string? appearanceMessage)
    {
        static string Time(DateTimeOffset value) => $"""
            <time datetime="{value.ToString("O", CultureInfo.InvariantCulture)}">{value.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture)} UTC</time>
            """;
        var html = new StringBuilder("""
            <!doctype html>
            <html lang="tr"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>CTI · Güvenlik haberleri</title><style>
            """).Append(Styles).Append("""
            </style></head><body>
            <a class="skip" href="#main-content">İçeriğe geç</a>
            <div class="frame">
            <aside class="sidebar" aria-label="CTI çalışma alanı">
              <div class="brand">
                <span class="mark"><svg class="icon" viewBox="0 0 24 24" aria-hidden="true"><path d="M12 3 4.5 6v5.5c0 4.3 3.1 7.3 7.5 9.5 4.4-2.2 7.5-5.2 7.5-9.5V6L12 3Z"/><path d="m8.8 12 2.1 2.1 4.5-4.5"/></svg></span>
                <div class="brand-text"><b>CTI</b>
            """);
        if (appearance.Visible)
            html.Append($"<span id=\"brand-caption\" class=\"brand-caption\" dir=\"auto\">{Escape(appearance.Label)}</span>");
        html.Append("</div></div><nav class=\"side-nav\" aria-label=\"Ana menü\">");
        AppendNavigation(html, query, view, "news", "Haberler", """<rect x="4" y="4.5" width="16" height="15" rx="2"/><path d="M8 9h8M8 13h8m-8 3.5h4"/>""");
        AppendNavigation(html, query, view, "sources", "Kaynakları yönet", """<path d="M4 7h16M4 12h16M4 17h16M9 5v4m6 1v4m-4 1v4"/>""");
        AppendNavigation(html, query, view, "settings", "Ayarlar", """<path d="M12 3v2m0 14v2M3 12h2m14 0h2M5.6 5.6 7 7m10 10 1.4 1.4m0-12.8L17 7M7 17l-1.4 1.4"/><circle cx="12" cy="12" r="5.5"/><circle cx="12" cy="12" r="1.8"/>""");
        html.Append($"""
            </nav><div class="side-note"><strong>Public CTI</strong>Haberler bu cihazda saklanır.</div></aside>
            <main id="main-content" tabindex="-1">
            <div class="topline"><span>CTI / <strong>{(view == "settings" ? "Ayarlar" : view == "sources" ? "Kaynakları yönet" : "Haberler")}</strong></span>
              <span class="preview">Yerel arşiv</span></div>
            <div id="news-view"{HiddenUnless(view, "news")}>
              <header class="hero hero--news">
                <div><p class="eyebrow">01 / Haber akışı</p><h1>Güvenlik haberleri</h1><p class="intro">Takip ettiğin güvenlik kaynaklarının haberlerini tek yerde oku.</p></div>
                <div class="index-note"><p class="index-kicker">Yerel arşiv</p><p>Haberler bu cihazda saklanır. Filtreler yalnızca listeyi değiştirir.</p></div>
              </header>
            """);
        int enabled = sources.Count(entry => entry.State.Enabled);
        AppendArticles(html, news, query, enabled > 0, token, aiEnabled, summaryMessage);
        html.Append($"""
            </div><div id="sources-view"{HiddenUnless(view, "sources")}>
            <header class="hero"><p class="eyebrow">02 / Kaynaklar</p><h1 id="source-heading" tabindex="-1">Haber kaynakları</h1>
            <p class="intro">CTI’nin hangi yayıncılardan yeni haber toplayacağını seç.</p></header>
            <a class="back" href="{Escape(NewsUrl(query))}">← Haberlere dön</a>
            <section class="board" aria-labelledby="source-management-heading">
            <div class="source-section"><h2 id="source-management-heading">Haber toplama</h2><p>{sources.Count} kaynak · {enabled} açık. İşlem yapmak için bir kaynak seç.</p></div>
            <p class="source-note">Bir kaynağı kapatırsan daha önce kaydedilen haberler silinmez.</p>
            <div class="source-list">
            """);
        string? expanded = notice?.Id ?? (news.Articles.Count == 0 && query is { Source: null, Title.Length: 0, Page: 1 }
            ? sources.FirstOrDefault(entry => entry.State.Enabled).Source?.Id : null);
        foreach (var (source, state) in sources.OrderByDescending(entry => entry.State.Enabled))
        {
            var id = Escape(source.Id);
            var hidden = $"""
                <input type="hidden" name="{Escape(token.FormFieldName)}" value="{Escape(token.RequestToken)}">
                <input type="hidden" name="source" value="{id}">
                """;
            foreach (var field in query.Parameters())
                hidden += $"<input type=\"hidden\" name=\"{Escape(field.Key)}\" value=\"{Escape(field.Value)}\">";
            var last = state.LastAttemptSucceeded switch
            {
                true => "Son deneme başarılı",
                false => "Son deneme başarısız",
                null => "Henüz denenmedi"
            };
            html.Append($"""
            <article class="source-row source-item" id="source-{id}" aria-labelledby="label-{id}">
            <details class="source-card"{(expanded == source.Id ? " open" : "")}>
            <summary><span class="source-name" id="label-{id}">{Escape(source.Name)}</span>
              <small class="source-meta">{(state.Enabled ? "Kaynak açık" : "Kaynak kapalı")} ·
              {(state.Automatic ? "Otomatik toplama açık" : "Otomatik toplama kapalı")} · {last}</small></summary>
            <div class="source-actions">
            <form method="post" action="/refresh">
              {hidden}
              <button type="submit"{(state.Enabled ? "" : " disabled")} class="action">Haberleri getir</button>
            </form>
            <dl><dt>Son deneme</dt><dd>{(state.LastAttempt is { } attempt ? Time(attempt) : "Henüz denenmedi")}</dd>
              <dt>Son başarılı toplama</dt><dd>{(state.LastSuccess is { } success ? Time(success) : "Henüz yok")}</dd>
              <dt>Son sonuç</dt><dd>{(state.LastAttemptSucceeded switch
              {
                  true => $"Başarılı · {state.LastAdded} yeni haber",
                  false => "Kaynak okunamadı. Önceden kaydedilen haberler duruyor.",
                  null => "Henüz sonuç yok"
              })}</dd>
            </dl>
            <form class="settings" method="post" action="/settings/source">
              {hidden}
              <label><input type="checkbox" name="enabled"{(state.Enabled ? " checked" : "")}> Bu kaynaktan haber topla</label>
              <label><input type="checkbox" name="automatic"{(state.Automatic ? " checked" : "")}> Otomatik toplamayı aç</label>
              <small>Kaynak açıkken, CTI çalıştığı sürece yaklaşık saatte bir kontrol edilir.</small><br>
              <button type="submit" class="action">Ayarları kaydet</button>
            </form>
            """);
            if (notice?.Id == source.Id && message is not null)
                html.Append($"<p class=\"status\" role=\"status\">{Escape(message)}</p>");
            html.Append("</div></details></article>");
        }
        html.Append("</div></section></div>");
        AppendAppearance(html, token, query, view, appearance, appearanceMessage);
        return html.Append("</main></div></body></html>").ToString();
    }

    private static void AppendArticles(StringBuilder html, NewsPage news, NewsQuery query, bool sourceEnabled,
        AntiforgeryTokenSet token, bool aiEnabled, string? summaryMessage)
    {
        html.Append("""
            <section class="board" aria-labelledby="articles-heading">
            <div class="tools"><div class="tools-top"><h2 id="articles-heading" class="articles-heading" tabindex="-1">Son haberler</h2></div>
            <div class="search"><svg class="icon" viewBox="0 0 24 24" aria-hidden="true"><circle cx="10.5" cy="10.5" r="5.5"/><path d="m15 15 4 4"/></svg>
            <form method="get" action="/#articles-heading" role="search" aria-label="Kayıtlı haberlerde ara">
            """);
        if (query.Source is { } filter)
            html.Append($"<input type=\"hidden\" name=\"filter\" value=\"{Escape(filter.Id)}\">");
        html.Append($"""
            <input type="search" id="title-search" aria-label="Haber başlığında ara" placeholder="Haber başlığında ara…" name="q" value="{Escape(query.Title)}" maxlength="{NewsQuery.MaxTitleLength}">
            <button class="action action--primary search-submit" type="submit">Ara</button>
            """);
        if (query.Title.Length != 0)
            html.Append($"<a class=\"clear\" href=\"{Escape(NewsUrl(query with { Title = "", Page = 1 }))}\">Temizle</a>");
        html.Append("</form></div><nav class=\"filter-set\" aria-label=\"Haberleri kaynağa göre filtrele\"><p>Listede göster</p><div class=\"chips\">");
        AppendFilterLink(html, query, null);
        foreach (var source in FeedSource.Catalog)
            AppendFilterLink(html, query, source);
        html.Append("</div></nav></div>");
        if (summaryMessage is not null)
            html.Append($"<p class=\"status\" role=\"status\">{Escape(summaryMessage)}</p>");
        if (aiEnabled && news.Articles.Any(item => item.Excerpt is not null && item.TurkishSummary is null))
            html.Append("<small class=\"excerpt-note\">AI özeti isteğe bağlıdır. Düğmeye bastığında yalnız seçtiğin haberin başlığı ve RSS açıklaması Gemini’ye gönderilir.</small>");

        if (news.Articles.Count == 0)
            html.Append(query.Page > 1
                ? $"<p class=\"search-feedback\">Bu sayfada haber yok. <a href=\"{Escape(NewsUrl(query with { Page = 1 }))}\">İlk sayfaya dön</a>.</p>"
                : query.Title.Length > 0 ? "<p class=\"search-feedback\">Aramana uyan haber bulunamadı.</p>"
                    : query.Source is not null ? "<p class=\"search-feedback\">Bu kaynaktan henüz haber kaydedilmedi.</p>"
                    : $"<p class=\"search-feedback\">Henüz haber yok. <a href=\"{Escape(ViewUrl(query, "sources"))}\">{(sourceEnabled ? "Bir kaynaktan haber getir" : "Başlamak için bir kaynağı aç")}</a>.</p>");
        else
        {
            html.Append($"<div class=\"results-bar\"><strong>Sayfa {query.Page} / {news.TotalPages}</strong><span>En yeni önce · {news.Articles.Count} haber</span></div>");
            if (news.Articles.Any(item => item.Excerpt is { Length: > 0 }))
                html.Append("<small class=\"excerpt-note\">Kısa açıklamalar yayıncıların RSS akışlarından alınır.</small>");
            html.Append("<ul class=\"articles\" aria-label=\"Haberler\">");
            foreach (var item in news.Articles)
            {
                // Treat stored values as untrusted as well as incoming RSS values.
                var publisher = FeedSource.Catalog.FirstOrDefault(source => source.Name == item.Source);
                var link = publisher?.CanonicalLink(item.Url);
                var when = item.Published is { } date
                    ? " · " + date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)
                    : "";
                string badge = publisher?.Id switch
                {
                    "microsoft-security" => "MS", "bleepingcomputer-security" => "BC", "krebs-on-security" => "KS",
                    "the-hacker-news" => "TH", "securityweek" => "SW", _ => "RSS"
                };
                html.Append($"<li id=\"{ArticleAnchor(item.Url)}\"><article class=\"story\"><span class=\"tile\" aria-hidden=\"true\">{badge}</span><div>");
                html.Append($"<div class=\"story-meta\"><b>{Escape(item.Source)}</b>{Escape(when)}</div><h3>");
                if (link is null) html.Append(Escape(item.Title));
                else html.Append($"<a class=\"story-link\" href=\"{Escape(link)}\" rel=\"noreferrer\">{Escape(item.Title)}</a>");
                html.Append("</h3>");
                if (item.Excerpt is { Length: > 0 } excerpt)
                    html.Append($"<p class=\"excerpt\">{Escape(excerpt)}</p>");
                if (item.TurkishSummary is { Length: > 0 } summary)
                    html.Append($"<div class=\"ai-summary\"><strong>Türkçe AI özeti</strong> <small>Yalnızca RSS bilgisine dayanır.</small><p>{Escape(summary)}</p></div>");
                else if (aiEnabled && item.Excerpt is { Length: > 0 } && item.Url.Length <= 2048)
                    AppendSummaryForm(html, item.Url, token, query);
                html.Append("</div></article></li>");
            }
            html.Append("</ul>");
        }
        AppendPagination(html, query, news.TotalPages);
        html.Append("</section>");
    }

    private static void AppendPagination(StringBuilder html, NewsQuery query, int total)
    {
        if (total < 1 || (total == 1 && query.Page == 1)) return;
        string Url(int page) => Escape(NewsUrl(query with { Page = page }));
        html.Append("<nav class=\"pagination\" aria-label=\"Haber sayfaları\">");
        if (query.Page > 1)
            html.Append($"<a href=\"{Url(Math.Min(query.Page - 1, total))}\" rel=\"prev\">Önceki</a>");

        int start = Math.Clamp(query.Page - 2, 1, Math.Max(1, total - 4));
        var numbers = Enumerable.Range(start, Math.Min(5, total)).Prepend(1).Append(total).Distinct().Order();
        int previous = 0;
        foreach (int page in numbers)
        {
            if (page - previous > 1) html.Append("<span class=\"gap\" aria-hidden=\"true\">…</span>");
            string number = page.ToString(CultureInfo.InvariantCulture);
            if (page == query.Page)
                html.Append($"<span aria-current=\"page\" aria-label=\"Sayfa {number}\">{number}</span>");
            else
                html.Append($"<a href=\"{Url(page)}\" aria-label=\"Sayfa {number}\">{number}</a>");
            previous = page;
        }
        if (query.Page < total)
            html.Append($"<a href=\"{Url(query.Page + 1)}\" rel=\"next\">Sonraki</a>");
        html.Append("</nav>");
    }

    private static void AppendFilterLink(StringBuilder html, NewsQuery query, FeedSource? source)
    {
        var url = NewsUrl(query with { Source = source, Page = 1 });
        bool selected = source?.Id == query.Source?.Id;
        html.Append($"<a class=\"chip\" href=\"{Escape(url)}\"{(selected ? " aria-current=\"page\"" : "")}>{Escape(source?.Name ?? "Tümü")}</a>");
    }

    private static string NewsUrl(NewsQuery query) => "/" + QueryString.Create(query.Parameters()) + "#articles-heading";
    private static string ViewUrl(NewsQuery query, string view) => "/" + QueryString.Create(view == "news"
        ? query.Parameters() : new KeyValuePair<string, string?>[] { new("view", view) }.Concat(query.Parameters()));

    private static string HiddenUnless(string view, string expected) => view == expected ? "" : " hidden";

    private static void AppendNavigation(StringBuilder html, NewsQuery query, string view, string dest, string label, string icon)
        => html.Append($"""
            <a class="nav-button" href="{Escape(ViewUrl(query, dest))}"{(view == dest ? " aria-current=\"page\"" : "")}><svg class="icon" viewBox="0 0 24 24" aria-hidden="true">{icon}</svg>{Escape(label)}</a>
            """);

    private static void AppendAppearance(StringBuilder html, AntiforgeryTokenSet token, NewsQuery query,
        string view, AppearanceSettings appearance, string? message)
    {
        html.Append($"""
            <div id="settings-view"{HiddenUnless(view, "settings")}>
            <header class="hero"><p class="eyebrow">03 / Tercihler</p><h1 id="settings-heading" tabindex="-1">Ayarlar</h1>
            <p class="intro">CTI başlığının altındaki yazıyı kişiselleştir.</p></header>
            <section class="board settings-board" aria-labelledby="appearance-heading">
            <div class="settings-section"><h2 id="appearance-heading">Menü alt yazısı</h2>
            <p>CTI başlığı kalır. Altındaki yazıyı değiştirebilir veya gizleyebilirsin.</p></div>
            <form class="settings-form" method="post" action="/settings/appearance">
            <input type="hidden" name="{Escape(token.FormFieldName)}" value="{Escape(token.RequestToken)}">
            """);
        foreach (var field in query.Parameters())
            html.Append($"<input type=\"hidden\" name=\"{Escape(field.Key)}\" value=\"{Escape(field.Value)}\">");
        html.Append($"""
            <label class="show-choice"><input type="checkbox" name="visible"{(appearance.Visible ? " checked" : "")}> CTI’nin altında yazı göster</label>
            <div class="brand-field"><label for="brand-label">Alt yazı</label>
            <input id="brand-label" name="label" type="text" value="{Escape(appearance.Label)}" maxlength="{AppearanceSettings.MaxLabelLength}" autocomplete="off" dir="auto" aria-describedby="brand-help" required>
            <p id="brand-help">En fazla 48 karakter. Varsayılan: Emecworks.</p></div>
            <div class="settings-actions"><button type="submit" name="mode" value="save" class="action action--primary">Kaydet</button>
            <button type="submit" name="mode" value="reset" class="action" formnovalidate>Varsayılana dön</button></div>
            """);
        if (message is not null)
            html.Append($"<p class=\"settings-status\" role=\"status\">{Escape(message)}</p>");
        html.Append("</form></section></div>");
    }

    private static string LoadStyles()
    {
        using var stream = typeof(Dashboard).Assembly.GetManifestResourceStream("CtiCore.Dashboard.css")
            ?? throw new InvalidOperationException("The dashboard stylesheet is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    internal static string ArticleAnchor(string url) =>
        "news-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)).AsSpan(0, 12)).ToLowerInvariant();

    private static void AppendSummaryForm(StringBuilder html, string url, AntiforgeryTokenSet token, NewsQuery query)
    {
        html.Append($"""
            <form class="summary-action" method="post" action="/summary">
            <input type="hidden" name="{Escape(token.FormFieldName)}" value="{Escape(token.RequestToken)}">
            <input type="hidden" name="article" value="{Escape(url)}">
            """);
        foreach (var field in query.Parameters())
            html.Append($"<input type=\"hidden\" name=\"{Escape(field.Key)}\" value=\"{Escape(field.Value)}\">");
        html.Append("<button class=\"action\" type=\"submit\">Türkçe AI özeti oluştur</button></form>");
    }

    private static string Escape(string? value) => HtmlEncoder.Default.Encode(value ?? "");
}
