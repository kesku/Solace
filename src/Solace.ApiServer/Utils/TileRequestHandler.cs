using Serilog;
using Solace.Common;
using Solace.DB;
using Solace.EventBus.Client;
using Solace.ObjectStore.Client;

namespace Solace.ApiServer.Utils;

// Lets other components (the tappables generator) get map tiles through the same cache as the client, so each tile is only rendered once
internal static class TileRequestHandler
{
    public static async Task StartAsync(EventBusClient eventBusClient, IServiceScopeFactory scopeFactory)
        => await eventBusClient.AddRequestHandlerAsync("tileCache", new RequestHandlerLister(
            async request =>
            {
                if (request.Type != "getTile")
                {
                    return null;
                }

                GetTileRequest? getTileRequest;
                try
                {
                    getTileRequest = Json.Deserialize<GetTileRequest>(request.Data);
                }
                catch (Exception ex)
                {
                    Log.Error($"Could not deserialise getTile request: {ex}");
                    return null;
                }

                if (getTileRequest is null || getTileRequest.TileX < 0 || getTileRequest.TileX >= 1 << 16 || getTileRequest.TileY < 0 || getTileRequest.TileY >= 1 << 16)
                {
                    return null;
                }

                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var earthDb = scope.ServiceProvider.GetRequiredService<EarthDbContext>();
                    var objectStore = scope.ServiceProvider.GetRequiredService<ObjectStoreClient>();
                    var tileCache = scope.ServiceProvider.GetRequiredService<TileCacheService>();

                    using var tilePng = new MemoryStream();
                    if (!await TileUtils.TryWriteTile(getTileRequest.TileX, getTileRequest.TileY, tilePng, earthDb, eventBusClient, objectStore, tileCache, CancellationToken.None))
                    {
                        return null;
                    }

                    return Convert.ToBase64String(tilePng.GetBuffer(), 0, (int)tilePng.Length);
                }
                catch (Exception ex)
                {
                    Log.Warning($"Could not get tile ({getTileRequest.TileX}, {getTileRequest.TileY}) for getTile request: {ex.Message}");
                    return null;
                }
            },
            async () => Log.Error("Tile cache event bus request handler error")));

    private sealed record GetTileRequest(
        int TileX,
        int TileY
    );
}
