using System.Globalization;
using System.Xml;
using Microsoft.Data.Sqlite;

namespace CtiCore;

internal sealed class CollectionCoordinator(
    ArticleStore store, FeedClient feed, TimeProvider clock, ILogger<CollectionCoordinator> logger)
{
    internal static readonly TimeSpan AutomaticInterval = TimeSpan.FromHours(1);
    private int collecting;

    public async Task<string> Refresh(bool automatic, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool acquired = false;
        try
        {
            // A scheduler tick that has nothing to do must not block a manual request.
            if (automatic && !IsDue(store.ReadSource())) return "not-due";
            if (Interlocked.CompareExchange(ref collecting, 1, 0) != 0) return "busy";
            acquired = true;
            var state = store.ReadSource();
            if (!state.Enabled) return "disabled";
            if (automatic && !IsDue(state)) return "not-due";

            IReadOnlyList<Article>? news;
            try
            {
                news = await feed.Read(FeedSource.Microsoft, cancellationToken);
            }
            catch (Exception ex) when ((ex is HttpRequestException or InvalidDataException or IOException or XmlException
                or OperationCanceledException) && !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("Reviewed RSS feed could not be fetched or parsed ({Type}).", ex.GetType().Name);
                news = null;
            }
            cancellationToken.ThrowIfCancellationRequested();
            var added = store.RecordCollection(state.Revision, automatic, clock.GetUtcNow(), news);
            return added is null ? "changed" : news is null ? "failed" : added.Value.ToString(CultureInfo.InvariantCulture);
        }
        catch (SqliteException)
        {
            logger.LogWarning("Could not read or update local CTI collection state.");
            return "storage";
        }
        finally
        {
            if (acquired) Volatile.Write(ref collecting, 0);
        }
    }

    private bool IsDue(SourceState state) => state.Enabled && state.Automatic &&
        (state.LastAttempt is null || clock.GetUtcNow() - state.LastAttempt.Value >= AutomaticInterval);
}

internal sealed class AutomaticCollector(CollectionCoordinator collection, TimeSpan pollInterval) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(pollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await collection.Refresh(automatic: true, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
