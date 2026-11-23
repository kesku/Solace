using Serilog;
using System.Threading.Channels;
using Solace.Common;
using Solace.Common.Utils;
using Solace.EventBus.Client;

namespace Solace.TappablesGenerator;

// Fetches map tiles from the api server's tile cache (which renders them if needed) and keeps a limited number of them in memory.
// If tiles can't be fetched (e.g. tile rendering is disabled), lookups report the tile as unavailable and tappables spawn anywhere in it.
public sealed class TileClassificationCache
{
    private static readonly int MAX_CACHED_TILES = 1024; // ~16 KB each
    private static readonly int FETCH_CONCURRENCY = 4;
    private static readonly TimeSpan REQUEST_TIMEOUT = TimeSpan.FromSeconds(10);
    private static readonly long UNAVAILABLE_TILE_RETRY_TIME = 10 * 60 * 1000;
    private static readonly long MIN_FAILURE_BACKOFF = 1 * 60 * 1000;
    private static readonly long MAX_FAILURE_BACKOFF = 30 * 60 * 1000;

    private readonly EventBusClient _eventBusClient;

    private readonly Lock _lock = new();
    private readonly Dictionary<long, LinkedListNode<Entry>> _entries = [];
    private readonly LinkedList<Entry> _lru = new(); // most recently used first
    private readonly Dictionary<long, TaskCompletionSource> _pendingTiles = [];
    private readonly Channel<(int TileX, int TileY)> _fetchQueue = Channel.CreateUnbounded<(int TileX, int TileY)>();

    // after a failed fetch, don't fetch any tiles until this time (with exponential backoff while the failures continue)
    private long _unavailableUntil;
    private int _consecutiveFailures;
    private bool _warned;

    private TileClassificationCache(EventBusClient eventBusClient)
    {
        _eventBusClient = eventBusClient;
    }

    public static TileClassificationCache Start(EventBusClient eventBusClient)
    {
        var cache = new TileClassificationCache(eventBusClient);

        for (int i = 0; i < FETCH_CONCURRENCY; i++)
        {
            Task.Run(cache.ProcessFetchQueueAsync).Forget();
        }

        return cache;
    }

    // Returns false if the tile is still being fetched (a fetch is started if needed).
    // Otherwise returns true, with classification set to null if the tile is not available.
    public bool TryGet(int tileX, int tileY, out TileClassification? classification)
    {
        long key = ToKey(tileX, tileY);
        long currentTime = U.CurrentTimeMillis();

        lock (_lock)
        {
            if (_entries.TryGetValue(key, out var node))
            {
                if (node.Value.Classification is not null || currentTime < node.Value.RetryTime)
                {
                    _lru.Remove(node);
                    _lru.AddFirst(node);

                    classification = node.Value.Classification;
                    return true;
                }

                _lru.Remove(node);
                _entries.Remove(key);
            }

            classification = null;

            if (_pendingTiles.ContainsKey(key))
            {
                return false;
            }

            if (currentTime < _unavailableUntil)
            {
                return true;
            }

            _pendingTiles[key] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _fetchQueue.Writer.TryWrite((tileX, tileY));

            return false;
        }
    }

    // Starts fetching the tiles that aren't cached yet and waits until they are fetched, or until maxWait has passed
    public async Task PrefetchAsync(IEnumerable<ActiveTiles.ActiveTile> tiles, TimeSpan maxWait)
    {
        List<Task> pendingTasks = [];
        foreach (ActiveTiles.ActiveTile tile in tiles)
        {
            if (!TryGet(tile.TileX, tile.TileY, out _))
            {
                lock (_lock)
                {
                    if (_pendingTiles.TryGetValue(ToKey(tile.TileX, tile.TileY), out var pendingTile))
                    {
                        pendingTasks.Add(pendingTile.Task);
                    }
                }
            }
        }

        if (pendingTasks.Count == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(pendingTasks).WaitAsync(maxWait);
        }
        catch (TimeoutException)
        {
            // the remaining tiles will be spawned once they have been fetched
        }
    }

    private async Task ProcessFetchQueueAsync()
    {
        await foreach (var (tileX, tileY) in _fetchQueue.Reader.ReadAllAsync())
        {
            long key = ToKey(tileX, tileY);

            bool fetch;
            lock (_lock)
            {
                fetch = U.CurrentTimeMillis() >= _unavailableUntil;
            }

            TileClassification? classification = null;
            string? failureReason = null;
            if (fetch)
            {
                try
                {
                    classification = await FetchAsync(tileX, tileY);
                    if (classification is null)
                    {
                        failureReason = "the api server did not return the tile, tile rendering may be disabled";
                    }
                }
                catch (TimeoutException)
                {
                    failureReason = $"request timed out after {REQUEST_TIMEOUT.TotalSeconds} seconds";
                }
                catch (Exception ex)
                {
                    failureReason = ex.Message;
                }
            }

            bool warn = false;
            bool recovered = false;
            long retryDelay = 0;
            TaskCompletionSource? pendingTile;
            lock (_lock)
            {
                if (fetch)
                {
                    long currentTime = U.CurrentTimeMillis();
                    AddEntry(key, new Entry(key, classification, classification is null ? currentTime + UNAVAILABLE_TILE_RETRY_TIME : long.MaxValue));

                    if (classification is not null)
                    {
                        _consecutiveFailures = 0;
                        recovered = _warned;
                        _warned = false;
                    }
                    else
                    {
                        // concurrent fetches that fail during the same outage only count once
                        if (currentTime >= _unavailableUntil)
                        {
                            _consecutiveFailures++;
                            _unavailableUntil = currentTime + long.Min(MIN_FAILURE_BACKOFF << int.Min(_consecutiveFailures - 1, 16), MAX_FAILURE_BACKOFF);
                        }

                        retryDelay = _unavailableUntil - currentTime;
                        warn = !_warned;
                        _warned = true;
                    }
                }

                _pendingTiles.Remove(key, out pendingTile);
            }

            pendingTile?.TrySetResult();

            if (warn)
            {
                Log.Warning($"Could not get map tile {tileX},{tileY} to avoid spawning tappables on water/buildings ({failureReason}), not fetching map tiles for {retryDelay / 1000} seconds, tappables will spawn anywhere in tiles that are not cached until map tiles are available again");
            }
            else if (failureReason is not null)
            {
                Log.Debug($"Could not get map tile {tileX},{tileY} ({failureReason}), not fetching map tiles for {retryDelay / 1000} seconds");
            }

            if (recovered)
            {
                Log.Information("Map tiles are available again, tappables will avoid water/buildings");
            }
        }
    }

    private async Task<TileClassification?> FetchAsync(int tileX, int tileY)
    {
        RequestSender requestSender = await _eventBusClient.AddRequestSenderAsync();
        try
        {
            string? tilePng64 = await requestSender.RequestAsync("tileCache", "getTile", Json.Serialize(new GetTileRequest(tileX, tileY))).WaitAsync(REQUEST_TIMEOUT);

            return tilePng64 is null ? null : TileClassification.FromPng(Convert.FromBase64String(tilePng64));
        }
        finally
        {
            // not DisposeAsync, which would wait for the request to finish if it timed out
            await requestSender.CloseAsync();
        }
    }

    private void AddEntry(long key, Entry entry)
    {
        if (_entries.Remove(key, out var existingNode))
        {
            _lru.Remove(existingNode);
        }

        _entries[key] = _lru.AddFirst(entry);

        while (_entries.Count > MAX_CACHED_TILES)
        {
            LinkedListNode<Entry> leastRecentlyUsed = _lru.Last!;
            _lru.RemoveLast();
            _entries.Remove(leastRecentlyUsed.Value.Key);
        }
    }

    private static long ToKey(int tileX, int tileY)
        => ((long)tileX << 32) | (uint)tileY;

    private sealed record Entry(
        long Key,
        TileClassification? Classification,
        long RetryTime
    );

    private sealed record GetTileRequest(
        int TileX,
        int TileY
    );
}
