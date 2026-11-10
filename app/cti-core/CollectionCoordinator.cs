using System.Collections.Concurrent;
using System.Globalization;
using System.Xml;
using Microsoft.Data.Sqlite;

namespace CtiCore;

internal sealed class CollectionCoordinator(
    ArticleStore store, FeedClient feed, TimeProvider clock, ILogger<CollectionCoordinator> logger)
{
    internal static readonly TimeSpan AutomaticInterval = TimeSpan.FromHours(1);
    private readonly ConcurrentDictionary<string, byte> collecting = new(StringComparer.Ordinal);

    public async Task<string> Refresh(FeedSource source, bool automatic, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool acquired = false;
        try
        {
            // A scheduler tick that has nothing to do must not block a manual request.
            if (automatic && !IsDue(store.ReadSource(source))) return "not-due";
            if (!collecting.TryAdd(source.Id, 0)) return "busy";
            acquired = true;
            var state = store.ReadSource(source);
            if (!state.Enabled) return "disabled";
            if (automatic && !IsDue(state)) return "not-due";

            IReadOnlyList<Article>? news;
            try
            {
                news = await feed.Read(source, cancellationToken);
            }
            catch (Exception ex) when ((ex is HttpRequestException or InvalidDataException or IOException or XmlException
                or OperationCanceledException) && !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("RSS source {Source} could not be fetched or parsed ({Type}).", source.Id, ex.GetType().Name);
                news = null;
            }
            cancellationToken.ThrowIfCancellationRequested();
            var added = store.RecordCollection(source, state.Revision, automatic, clock.GetUtcNow(), news);
            return added is null ? "changed" : news is null ? "failed" : added.Value.ToString(CultureInfo.InvariantCulture);
        }
        catch (SqliteException)
        {
            logger.LogWarning("Could not read or update local CTI collection state.");
            return "storage";
        }
        finally
        {
            if (acquired) collecting.TryRemove(source.Id, out _);
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
                await Parallel.ForEachAsync(FeedSource.Catalog,
                    new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = stoppingToken },
                    async (source, token) => { await collection.Refresh(source, automatic: true, token); });
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
