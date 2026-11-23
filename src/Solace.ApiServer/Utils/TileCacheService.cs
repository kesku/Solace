using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Solace.DB;
using Solace.ObjectStore.Client;

namespace Solace.ApiServer.Utils;

/// <summary>
/// Keeps the rendered tile cache within <see cref="MaxTiles"/> by evicting the least recently used tiles.
/// </summary>
/// <remarks>
/// Tile accesses are only recorded in memory on the request path, they are written to the database
/// and the cache size is checked in batches by a background loop.
/// </remarks>
public sealed class TileCacheService : BackgroundService
{
    private static readonly TimeSpan MaintenanceInterval = TimeSpan.FromMinutes(1);
    private const int BatchSize = 500;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ObjectStoreClient _objectStore;

    private ConcurrentDictionary<ulong, byte> _accessedTiles = new();
    private int _tilesAdded = 1; // check the cache size on startup, the limit might have been lowered

    public TileCacheService(int maxTiles, IServiceScopeFactory scopeFactory, ObjectStoreClient objectStore)
    {
        MaxTiles = Math.Max(maxTiles, 0);
        _scopeFactory = scopeFactory;
        _objectStore = objectStore;
    }

    /// <summary>
    /// Maximum number of cached tiles, 0 means unlimited.
    /// </summary>
    public int MaxTiles { get; }

    public void RecordAccess(ulong tileId)
        => _accessedTiles.TryAdd(tileId, 0);

    public void RecordAdded()
        => Volatile.Write(ref _tilesAdded, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Log.Information(MaxTiles > 0 ? $"Tile cache limited to {MaxTiles} tiles" : "Tile cache size is unlimited");

        using var timer = new PeriodicTimer(MaintenanceInterval);

        try
        {
            do
            {
                try
                {
                    await RunMaintenanceAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.Error($"Tile cache maintenance failed: {ex}");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task RunMaintenanceAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var earthDb = scope.ServiceProvider.GetRequiredService<EarthDbContext>();

        await FlushAccessesAsync(earthDb, cancellationToken);

        if (MaxTiles > 0 && Interlocked.Exchange(ref _tilesAdded, 0) != 0)
        {
            await EvictAsync(earthDb, cancellationToken);
        }
    }

    private async Task FlushAccessesAsync(EarthDbContext earthDb, CancellationToken cancellationToken)
    {
        var accessedTiles = Interlocked.Exchange(ref _accessedTiles, new ConcurrentDictionary<ulong, byte>());

        if (accessedTiles.IsEmpty)
        {
            return;
        }

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        foreach (ulong[] ids in accessedTiles.Keys.Chunk(BatchSize))
        {
            await earthDb.Tiles
                .Where(tile => ids.Contains(tile.Id))
                .ExecuteUpdateAsync(setters => setters.SetProperty(tile => tile.LastAccessed, now), cancellationToken);
        }
    }

    private async Task EvictAsync(EarthDbContext earthDb, CancellationToken cancellationToken)
    {
        int count = await earthDb.Tiles.CountAsync(cancellationToken);
        int excess = count - MaxTiles;

        if (excess <= 0)
        {
            return;
        }

        Log.Information($"Tile cache contains {count} tiles (limit {MaxTiles}), evicting {excess} least recently used");

        int evicted = 0;
        while (evicted < excess)
        {
            var tiles = await earthDb.Tiles
                .AsNoTracking()
                .OrderBy(tile => tile.LastAccessed)
                .Take(Math.Min(excess - evicted, BatchSize))
                .Select(tile => new { tile.Id, tile.ObjectStoreId })
                .ToListAsync(cancellationToken);

            if (tiles.Count is 0)
            {
                break;
            }

            ulong[] ids = [.. tiles.Select(tile => tile.Id)];

            // delete the rows first, a missing row just causes a re-render, a row pointing to a missing object would not
            await earthDb.Tiles
                .Where(tile => ids.Contains(tile.Id))
                .ExecuteDeleteAsync(cancellationToken);

            foreach (var tile in tiles)
            {
                await TryDeleteObjectAsync(_objectStore, tile.ObjectStoreId);
            }

            evicted += tiles.Count;
        }

        Log.Information($"Evicted {evicted} tiles from tile cache");
    }

    internal static async Task TryDeleteObjectAsync(ObjectStoreClient objectStore, string objectId)
    {
        try
        {
            if (!await objectStore.DeleteAsync(objectId))
            {
                Log.Warning($"Failed to delete tile object {objectId} from object store");
            }
        }
        catch (Exception ex)
        {
            Log.Warning($"Failed to delete tile object {objectId} from object store: {ex}");
        }
    }
}
