using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Antiforgery;

namespace CtiCore;

internal static class Dashboard
{
    internal static string Render(IReadOnlyList<Article> articles, IReadOnlyList<(FeedSource Source, SourceState State)> sources,
        AntiforgeryTokenSet token, FeedSource? notice, string? message, FeedSource? filter)
    {
        static string Time(DateTimeOffset value) => $"""
            <time datetime="{value.ToString("O", CultureInfo.InvariantCulture)}">{value.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture)} UTC</time>
            """;
        var html = new StringBuilder("""
            <!doctype html>
            <html lang="tr"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>CTI · Haberler</title>
            <style>
            :root{font-family:system-ui,sans-serif;color:#162832;background:#eff4f6;color-scheme:light}
            body{margin:0;padding:clamp(1rem,5vw,3.5rem)} main{max-width:760px;margin:auto}
            header{margin-bottom:1.5rem} h1,h2,p{margin:0 0 .75rem} h1{font-size:2rem} h2{font-size:1.25rem}
            section{background:#fff;border:1px solid #cddbe0;border-radius:12px;padding:1.25rem;margin:1rem 0}
            form{margin:.75rem 0} button{font:inherit;padding:.55rem .75rem;border-radius:6px;border:1px solid #708690}
            button{background:#095b67;color:white;font-weight:650;cursor:pointer} :focus-visible{outline:3px solid #087286;outline-offset:3px}
            button:disabled{background:#e3eaed;color:#526973;cursor:default}
            .settings{border-top:1px solid #d6e2e6;padding-top:.75rem} .settings label{display:block;margin:.4rem 0}
            .settings button{background:#fff;color:#075565;margin-top:.5rem}
            dl{margin:.75rem 0} dt{font-weight:600} dd{margin:0 0 .45rem}
            .source-card{border-top:1px solid #d6e2e6}
            .source-card summary{cursor:pointer;padding:.75rem 0}
            .source-name{font-size:1.04rem;font-weight:650}
            .source-meta{display:block;margin:.24rem 0 0 1.1rem}
            .source-actions{padding:0 .4rem .5rem 1.1rem}
            a{color:#075565} ul{list-style:none;margin:0;padding:0} li{padding:.8rem 0;border-top:1px solid #d6e2e6}
            .filters{display:flex;flex-wrap:wrap;gap:.4rem;margin:.7rem 0 1rem}
            .filters a{border:1px solid #cddbe0;border-radius:6px;padding:.3rem .55rem}
            .filters a[aria-current]{background:#095b67;color:#fff;font-weight:600}
            small{color:#425c68} li small{display:block;margin-top:.3rem} .status{margin:1rem 0 0;padding:.7rem;background:#e1f4f1}
            </style></head><body><main>
            <header><small>EMECWORKS / CTI</small><h1>Güvenlik haberleri</h1>
            <p>Haberler bu cihazda saklanır. <a href="#source-heading">Haber kaynaklarını yönet</a></p></header>
            """);
        int enabled = sources.Count(entry => entry.State.Enabled);
        AppendArticles(html, articles, filter, enabled > 0);
        html.Append($"""
            <section aria-labelledby="source-heading"><h2 id="source-heading">Haber kaynakları</h2>
            <p>{sources.Count} kaynak · {enabled} açık. Haber getirmek veya ayarları değiştirmek için bir kaynak seç.</p>
            """);
        string? expanded = notice?.Id ?? (articles.Count == 0 && filter is null
            ? sources.FirstOrDefault(entry => entry.State.Enabled).Source?.Id : null);
        foreach (var (source, state) in sources.OrderByDescending(entry => entry.State.Enabled))
        {
            var id = Escape(source.Id);
            var hidden = $"""
                <input type="hidden" name="{Escape(token.FormFieldName)}" value="{Escape(token.RequestToken)}">
                <input type="hidden" name="source" value="{id}">
                """;
            if (filter is not null)
                hidden += $"<input type=\"hidden\" name=\"filter\" value=\"{Escape(filter.Id)}\">";
            var last = state.LastAttemptSucceeded switch
            {
                true => "Son deneme başarılı",
                false => "Son deneme başarısız",
                null => "Henüz denenmedi"
            };
            html.Append($"""
            <article id="source-{id}" aria-labelledby="label-{id}">
            <details class="source-card"{(expanded == source.Id ? " open" : "")}>
            <summary><span class="source-name" id="label-{id}">{Escape(source.Name)}</span>
              <small class="source-meta">{(state.Enabled ? "Kaynak açık" : "Kaynak kapalı")} ·
              {(state.Automatic ? "Otomatik toplama açık" : "Otomatik toplama kapalı")} · {last}</small></summary>
            <div class="source-actions">
            <form method="post" action="/refresh">
              {hidden}
              <button type="submit"{(state.Enabled ? "" : " disabled")}>Haberleri getir</button>
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
              <button type="submit">Ayarları kaydet</button>
            </form>
            """);
            if (notice?.Id == source.Id && message is not null)
                html.Append($"<p class=\"status\" role=\"status\">{Escape(message)}</p>");
            html.Append("</div></details></article>");
        }
        return html.Append("</section></main></body></html>").ToString();
    }

    private static void AppendArticles(StringBuilder html, IReadOnlyList<Article> articles, FeedSource? filter, bool sourceEnabled)
    {
        html.Append("""
            <section aria-labelledby="articles-heading"><h2 id="articles-heading">Son haberler</h2>
            <nav class="filters" aria-label="Haberleri kaynağa göre filtrele">
            """);
        AppendFilterLink(html, null, filter is null);
        foreach (var source in FeedSource.Catalog)
            AppendFilterLink(html, source, source.Id == filter?.Id);
        html.Append("</nav>");
        if (articles.Count == 0)
            html.Append(filter is not null
                ? "<p>Bu kaynaktan henüz haber kaydedilmedi.</p>"
                : sourceEnabled
                    ? "<p>Henüz haber yok. <a href=\"#source-heading\">Bir kaynaktan haber getir</a>.</p>"
                    : "<p>Henüz haber yok. Başlamak için <a href=\"#source-heading\">bir kaynağı aç</a>.</p>");
        else
        {
            html.Append("<ul>");
            foreach (var item in articles)
            {
                // Treat stored values as untrusted as well as incoming RSS values.
                var publisher = FeedSource.Catalog.FirstOrDefault(source => source.Name == item.Source);
                var link = publisher?.CanonicalLink(item.Url);
                html.Append("<li>");
                if (link is null) html.Append(Escape(item.Title));
                else html.Append($"<a href=\"{Escape(link)}\" rel=\"noreferrer\">{Escape(item.Title)}</a>");
                var when = item.Published is { } date
                    ? " · " + date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)
                    : "";
                html.Append($"<small>{Escape(item.Source + when)}</small></li>");
            }
            html.Append("</ul>");
        }
        html.Append("</section>");
    }

    private static void AppendFilterLink(StringBuilder html, FeedSource? source, bool selected)
    {
        var url = source is null ? "/#articles-heading" : $"/?filter={Uri.EscapeDataString(source.Id)}#articles-heading";
        html.Append($"<a href=\"{Escape(url)}\"{(selected ? " aria-current=\"page\"" : "")}>{Escape(source?.Name ?? "Tümü")}</a>");
    }

    private static string Escape(string? value) => HtmlEncoder.Default.Encode(value ?? "");
}
