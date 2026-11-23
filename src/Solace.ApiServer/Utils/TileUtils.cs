using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Solace.Common;
using Solace.DB;
using Solace.EventBus.Client;
using Solace.ObjectStore.Client;

namespace Solace.ApiServer.Utils;

internal static class TileUtils
{
    // renders that are in progress in this process, keyed by tile id, so concurrent requests for the same tile
    // (e.g. the client map and the tappables generator entering a new area) share one render instead of rendering it twice
    private static readonly ConcurrentDictionary<ulong, Task<byte[]?>> RendersInProgress = new();

    public static async Task<bool> TryWriteTile(int tileX, int tileY, Stream dest, EarthDbContext earthDb, IServiceScopeFactory scopeFactory, EventBusClient eventBus, ObjectStoreClient objectStore, TileCacheService tileCache, CancellationToken cancellationToken)
    {
        ulong dbPos = ToDbPos(tileX, tileY);

        var tile = await earthDb.Tiles
            .AsNoTracking()
            .FirstOrDefaultAsync(tile => tile.Id == dbPos, cancellationToken: cancellationToken);

        if (tile is not null)
        {
            if (await TryWriteTileFromObject(tile.ObjectStoreId, dest, objectStore, cancellationToken))
            {
                tileCache.RecordAccess(dbPos);
                return true;
            }

            // the object might have been evicted after the tile was looked up, render the tile again
            Log.Warning($"Object {tile.ObjectStoreId} of tile ({tileX}, {tileY}) is missing, rendering tile again");

            // only delete the row that points to the missing object, not one inserted by a concurrent render
            string missingObjectId = tile.ObjectStoreId;
            await earthDb.Tiles
                .Where(tile => tile.Id == dbPos && tile.ObjectStoreId == missingObjectId)
                .ExecuteDeleteAsync(cancellationToken);
        }

        // the render is shared with other requests, so a cancelled request only stops waiting for it
        byte[]? tilePng = await GetOrStartRender(tileX, tileY, dbPos, scopeFactory, eventBus, objectStore, tileCache)
            .WaitAsync(cancellationToken);

        if (tilePng is null)
        {
            return false;
        }

        await dest.WriteAsync(tilePng, cancellationToken);

        return true;
    }

    private static Task<byte[]?> GetOrStartRender(int tileX, int tileY, ulong dbPos, IServiceScopeFactory scopeFactory, EventBusClient eventBus, ObjectStoreClient objectStore, TileCacheService tileCache)
    {
        var render = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);

        var existingRender = RendersInProgress.GetOrAdd(dbPos, render.Task);
        if (existingRender != render.Task)
        {
            return existingRender;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                render.SetResult(await RenderAndStoreTile(tileX, tileY, dbPos, scopeFactory, eventBus, objectStore, tileCache));
            }
            catch (Exception ex)
            {
                render.SetException(ex);
            }
            finally
            {
                RendersInProgress.TryRemove(KeyValuePair.Create(dbPos, render.Task));
            }
        });

        return render.Task;
    }

    // runs detached from the request that started it, so it uses its own database context and is not cancelled with the request
    private static async Task<byte[]?> RenderAndStoreTile(int tileX, int tileY, ulong dbPos, IServiceScopeFactory scopeFactory, EventBusClient eventBus, ObjectStoreClient objectStore, TileCacheService tileCache)
    {
        Log.Information("Rendering tile");
        await using var requestSender = await eventBus.AddRequestSenderAsync();
        string? tilePng64 = await requestSender.RequestAsync("tile", "renderTile", Json.Serialize(new RenderTileRequest(tileX, tileY, 16)));

        if (tilePng64 is null)
        {
            Log.Warning("Could not get tile (tile renderer did not respond to event bus request)");
            return null;
        }

        byte[] tilePng = Convert.FromBase64String(tilePng64);

        var tileObjectId = await objectStore.StoreAsync(tilePng);

        if (string.IsNullOrEmpty(tileObjectId))
        {
            Log.Warning("Failed to store tile to object store");
            return null;
        }

        var tile = new DB.Models.Global.Tile()
        {
            Id = dbPos,
            ObjectStoreId = tileObjectId,
            LastAccessed = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };

        await using var scope = scopeFactory.CreateAsyncScope();
        var earthDb = scope.ServiceProvider.GetRequiredService<EarthDbContext>();

        earthDb.Tiles.Add(tile);

        try
        {
            await earthDb.SaveChangesAsync();

            tileCache.RecordAdded();

            Log.Debug($"Stored tile ({tileX}, {tileY}) to object store under id {tileObjectId}");
        }
        catch (DbUpdateException ex)
        {
            // e.g. the same tile was rendered concurrently by a request that did not share this render, the tile can still be served
            Log.Warning($"Failed to store tile ({tileX}, {tileY}) to database: {ex.Message}");

            await TileCacheService.TryDeleteObjectAsync(objectStore, tileObjectId);
        }

        return tilePng;
    }

    private static async Task<bool> TryWriteTileFromObject(string tileObjectId, Stream dest, ObjectStoreClient objectStoreClient, CancellationToken cancellationToken)
    {
        byte[]? tilePng = await objectStoreClient.GetAsync(tileObjectId);

        if (tilePng is null)
        {
            return false;
        }

        await dest.WriteAsync(tilePng, cancellationToken);

        return true;
    }

    private static ulong ToDbPos(int tileX, int tileY)
        => unchecked((ulong)((long)tileX | ((long)tileY << 32)));

    private sealed record RenderTileRequest(int TileX, int TileY, int Zoom);
}
