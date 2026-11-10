using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Antiforgery;

namespace CtiCore;

internal static class Dashboard
{
    internal static string Render(IReadOnlyList<Article> articles, AntiforgeryTokenSet token, string? message)
    {
        static string Escape(string? value) => HtmlEncoder.Default.Encode(value ?? "");
        var html = new StringBuilder("""
            <!doctype html>
            <html lang="tr"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>CTI · Haberler</title>
            <style>
            :root{font-family:system-ui,sans-serif;color:#162832;background:#eff4f6;color-scheme:light}
            body{margin:0;padding:clamp(1rem,5vw,3.5rem)} main{max-width:760px;margin:auto}
            header{margin-bottom:1.5rem} h1,h2,p{margin:0 0 .75rem} h1{font-size:2rem} h2{font-size:1.25rem}
            section{background:#fff;border:1px solid #cddbe0;border-radius:12px;padding:1.25rem;margin:1rem 0}
            form{display:flex;align-items:end;flex-wrap:wrap;gap:.75rem} label{display:grid;gap:.35rem;font-weight:600}
            select,button{font:inherit;padding:.55rem .75rem;border-radius:6px;border:1px solid #708690}
            button{background:#095b67;color:white;font-weight:650;cursor:pointer} :focus-visible{outline:3px solid #087286;outline-offset:3px}
            a{color:#075565} ul{list-style:none;margin:0;padding:0} li{padding:.8rem 0;border-top:1px solid #d6e2e6}
            small{color:#425c68} li small{display:block;margin-top:.3rem} .status{margin:1rem 0 0;padding:.7rem;background:#e1f4f1}
            </style></head><body><main>
            <header><small>EMECWORKS / CTI</small><h1>Güvenlik haberleri</h1>
            <p>Bir kaynak seç, son haberleri getir. Haberler bu cihazda saklanır.</p></header>
            <section aria-labelledby="source-heading"><h2 id="source-heading">Haber kaynağı</h2>
            """);
        html.Append($"""
            <form method="post" action="/refresh">
              <input type="hidden" name="{Escape(token.FormFieldName)}" value="{Escape(token.RequestToken)}">
              <label>Kaynak<select name="source"><option value="{Escape(FeedSource.Microsoft.Id)}">{Escape(FeedSource.Microsoft.Name)}</option></select></label>
              <button type="submit">Haberleri getir</button>
            </form>
            """);
        if (message is not null) html.Append($"<p class=\"status\" role=\"status\">{Escape(message)}</p>");
        html.Append("</section><section aria-labelledby=\"articles-heading\"><h2 id=\"articles-heading\">Son haberler</h2>");
        if (articles.Count == 0)
            html.Append("<p>Henüz haber yok. Başlamak için <strong>Haberleri getir</strong> düğmesine bas.</p>");
        else
        {
            html.Append("<ul>");
            foreach (var item in articles)
            {
                // Treat stored values as untrusted as well as incoming RSS values.
                var link = FeedSource.Microsoft.CanonicalLink(item.Url);
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
        return html.Append("</section></main></body></html>").ToString();
    }
}
