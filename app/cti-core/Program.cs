using System.Net;
using System.Xml;
using System.Xml.Linq;
using CtiCore;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Data.Sqlite;

var settings = CoreOptions.Parse(args);
using var transport = FeedClient.CreateTransport();
using var http = new HttpClient(transport);
await using var app = CoreApp.Create(settings, http);
await app.RunAsync();

namespace CtiCore
{
    internal sealed record CoreOptions(int Port, string DataDirectory)
    {
        internal static CoreOptions Parse(string[] args)
        {
            const int defaultPort = 8091;
            int port = defaultPort;
            string? data = null;
            for (int i = 0; i < args.Length; i += 2)
            {
                if (i + 1 == args.Length)
                    throw new ArgumentException("Usage: [--port 8091] [--data-dir PATH]");
                if (args[i] == "--port" && int.TryParse(args[i + 1], out var value) && value is >= 1024 and <= 65535)
                    port = value;
                else if (args[i] == "--data-dir" && !string.IsNullOrWhiteSpace(args[i + 1]))
                    data = Path.GetFullPath(args[i + 1]);
                else
                    throw new ArgumentException("Usage: [--port 8091] [--data-dir PATH]");
            }
            if (data is null)
            {
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrWhiteSpace(local))
                    throw new InvalidOperationException("Specify --data-dir for this account.");
                data = Path.Combine(local, "Emecworks", "CTI-Core");
            }
            return new CoreOptions(port, data);
        }
    }

    internal static class CoreApp
    {
        public static WebApplication Create(CoreOptions options, HttpClient publisher)
        {
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
            builder.WebHost.ConfigureKestrel(k =>
            {
                k.AddServerHeader = false;
                k.Limits.MaxRequestBodySize = 8192;
                k.Listen(IPAddress.Loopback, options.Port);
            });
            builder.Services.AddAntiforgery(o =>
            {
                o.Cookie.Name = "Emecworks.CtiCore.Antiforgery";
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = SameSiteMode.Strict;
                o.Cookie.IsEssential = true;
                o.Cookie.SecurePolicy = CookieSecurePolicy.None; // This preview uses loopback HTTP only.
            });
            builder.Services.AddDataProtection().AddKeyManagementOptions(o => o.XmlRepository = new MemoryKeyRepository());
            var app = builder.Build();
            var store = new ArticleStore(options.DataDirectory);
            var feed = new FeedClient(publisher);
            // Dispose the refresh gate only when the host is stopped, not when Create returns.
            var refreshGate = new SemaphoreSlim(1, 1);
            app.Lifetime.ApplicationStopped.Register(refreshGate.Dispose);

            app.Use(async (ctx, next) =>
            {
                string expected = Origin(ctx);
                if (!string.Equals(ctx.Request.Host.Value, expected["http://".Length..], StringComparison.Ordinal)
                    || !IPAddress.Loopback.Equals(ctx.Connection.RemoteIpAddress))
                {
                    ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }
                ctx.Response.Headers["Cache-Control"] = "no-cache, no-store";
                ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
                ctx.Response.Headers["Referrer-Policy"] = "same-origin";
                ctx.Response.Headers["Content-Security-Policy"] =
                    "default-src 'none'; style-src 'unsafe-inline'; form-action 'self'; base-uri 'none'; frame-ancestors 'none'";
                await next(ctx);
            });

            app.MapGet("/", (HttpContext ctx, IAntiforgery antiforgery) =>
            {
                var token = antiforgery.GetAndStoreTokens(ctx);
                var status = ctx.Request.Query["status"].ToString();
                var message = status switch
                {
                    "busy" => "Haberler zaten getiriliyor. Biraz sonra tekrar dene.",
                    "failed" => "Kaynak şu anda okunamadı. Kayıtlı haberlerin duruyor.",
                    "storage" => "Haberler kaydedilemedi. Biraz sonra tekrar dene.",
                    _ => int.TryParse(status, out int n) && n is >= 0 and <= FeedClient.MaximumItems
                        ? n == 0 ? "Yeni haber yok. Aynı haberler tekrar eklenmedi." : $"{n} yeni haber eklendi."
                        : null
                };
                try
                {
                    return Results.Content(Dashboard.Render(store.Latest(), token, message), "text/html; charset=utf-8");
                }
                catch (SqliteException)
                {
                    return Results.Text("Kayıtlı haberler şu anda okunamadı.", statusCode: 503);
                }
            });

            app.MapPost("/refresh", async (HttpContext ctx, IAntiforgery antiforgery) =>
            {
                if (!string.Equals(ctx.Request.Headers.Origin.ToString(), Origin(ctx), StringComparison.Ordinal)
                    || ctx.Request.ContentType?.Split(';')[0] != "application/x-www-form-urlencoded")
                    return Results.BadRequest();
                try
                {
                    await antiforgery.ValidateRequestAsync(ctx);
                    var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
                    if (form["source"].Count != 1 || form["source"][0] != FeedSource.Microsoft.Id)
                        return Results.BadRequest();
                }
                catch (AntiforgeryValidationException) { return Results.BadRequest(); }
                catch (InvalidDataException) { return Results.BadRequest(); }

                if (!await refreshGate.WaitAsync(0, ctx.RequestAborted))
                    return SeeOther(ctx, "busy");
                try
                {
                    var news = await feed.Read(FeedSource.Microsoft, ctx.RequestAborted);
                    ctx.RequestAborted.ThrowIfCancellationRequested();
                    return SeeOther(ctx, store.Save(news).ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                catch (Exception ex) when ((ex is HttpRequestException or InvalidDataException or IOException or XmlException
                    or OperationCanceledException) && !ctx.RequestAborted.IsCancellationRequested)
                {
                    app.Logger.LogWarning("Reviewed RSS feed could not be fetched or parsed ({Type}).", ex.GetType().Name);
                    return SeeOther(ctx, "failed");
                }
                catch (SqliteException)
                {
                    app.Logger.LogWarning("Could not save articles to the local SQLite file.");
                    return SeeOther(ctx, "storage");
                }
                finally { refreshGate.Release(); }
            });
            return app;
        }

        private static string Origin(HttpContext ctx) => $"http://127.0.0.1:{ctx.Connection.LocalPort}";

        private static IResult SeeOther(HttpContext ctx, string status)
        {
            ctx.Response.Headers.Location = "/?status=" + status;
            return Results.StatusCode(StatusCodes.Status303SeeOther);
        }

        // Forms expire when the preview process restarts; no keys go into a shared user profile.
        private sealed class MemoryKeyRepository : IXmlRepository
        {
            private readonly List<XElement> keys = [];
            public IReadOnlyCollection<XElement> GetAllElements()
            {
                lock (keys) return keys.Select(key => new XElement(key)).ToArray();
            }

            public void StoreElement(XElement element, string friendlyName)
            {
                lock (keys) keys.Add(new XElement(element));
            }
        }
    }
}
