using Serilog;
using Solace.Common;
using Solace.Common.Utils;
using Solace.EventBus.Client;

namespace Solace.TappablesGenerator;

public class Spawner
{
    private static readonly long SPAWN_INTERVAL = 15 * 1000;
    private static readonly TimeSpan NEW_TILE_CLASSIFICATION_WAIT = TimeSpan.FromSeconds(1);

    private readonly ActiveTiles _activeTiles;
    private readonly TappableGenerator _tappableGenerator;
    private readonly EncounterGenerator _encounterGenerator;
    private readonly TileClassificationCache _tileClassificationCache;
    private readonly Publisher _publisher;

    private readonly int _maxTappableLifetimeIntervals;

    private long _spawnCycleTime;
    private int _spawnCycleIndex;
    private readonly Dictionary<int, int> _lastSpawnCycleForTile = [];

    public Spawner(ActiveTiles activeTiles, TappableGenerator tappableGenerator, EncounterGenerator encounterGenerator, TileClassificationCache tileClassificationCache, Publisher publisher)
    {
        _activeTiles = activeTiles;

        _tappableGenerator = tappableGenerator;
        _encounterGenerator = encounterGenerator;
        _tileClassificationCache = tileClassificationCache;
        _publisher = publisher;

        _maxTappableLifetimeIntervals = (int)(long.Max(TappableGenerator.GetMaxTappableLifetime(), _encounterGenerator.GetMaxEncounterLifetime()) / SPAWN_INTERVAL + 1);

        _spawnCycleTime = U.CurrentTimeMillis();
        _spawnCycleIndex = _maxTappableLifetimeIntervals;
    }

    public static async Task<Spawner> CreateAsync(EventBusClient eventBusClient, ActiveTiles activeTiles, TappableGenerator tappableGenerator, EncounterGenerator encounterGenerator, TileClassificationCache tileClassificationCache)
    {
        var publisher = await eventBusClient.AddPublisherAsync();

        return new Spawner(activeTiles, tappableGenerator, encounterGenerator, tileClassificationCache, publisher);
    }

    public async Task Run()
    {
        long nextTime = U.CurrentTimeMillis() + SPAWN_INTERVAL;
        while (true)
        {
            try
            {
                Thread.Sleep(Math.Max(0, (int)(nextTime - U.CurrentTimeMillis())));
            }
            catch (ThreadInterruptedException)
            {
                Log.Information("Spawn thread was interrupted, exiting");
                break;
            }

            nextTime += SPAWN_INTERVAL;

            await DoSpawnCycle();
        }
    }

    [Obsolete($"Use {nameof(SpawnTiles)} instead.")]
    public async Task SpawnTile(int tileX, int tileY)
    {
        long spawnCycleTime = _spawnCycleTime;
        int spawnCycleIndex = _spawnCycleIndex;

        while (spawnCycleTime < U.CurrentTimeMillis())
        {
            spawnCycleTime += SPAWN_INTERVAL;
            spawnCycleIndex++;
        }

        List<Tappable> tappables = [];

        List<Encounter> encounters = [];
        DoSpawnCyclesForTile(tileX, tileY, spawnCycleTime, spawnCycleIndex, tappables, encounters);

        long tappableCutoffTime = spawnCycleTime - SPAWN_INTERVAL;
        tappables.RemoveAll(tappable => tappable.SpawnTime + tappable.ValidFor < tappableCutoffTime);
        encounters.RemoveAll(encounter => encounter.SpawnTime + encounter.ValidFor < tappableCutoffTime);

        await SendSpawnedTappables(tappables, encounters);
    }

    public async Task SpawnTiles(IEnumerable<ActiveTiles.ActiveTile> activeTiles)
    {
        // give the classifications of the new tiles a moment to load so they can spawn straight away,
        // tiles that are still loading are spawned by a later spawn cycle
        await _tileClassificationCache.PrefetchAsync(activeTiles, NEW_TILE_CLASSIFICATION_WAIT);

        long spawnCycleTime = _spawnCycleTime;
        int spawnCycleIndex = _spawnCycleIndex;

        while (spawnCycleTime < U.CurrentTimeMillis())
        {
            spawnCycleTime += SPAWN_INTERVAL;
            spawnCycleIndex++;
        }

        List<Tappable> tappables = [];
        List<Encounter> encounters = [];
        foreach (var activeTile in activeTiles)
        {
            DoSpawnCyclesForTile(activeTile.TileX, activeTile.TileY, spawnCycleTime, spawnCycleIndex, tappables, encounters);
        }

        long tappableCutoffTime = spawnCycleTime - SPAWN_INTERVAL;
        tappables.RemoveAll(tappable => tappable.SpawnTime + tappable.ValidFor < tappableCutoffTime);
        encounters.RemoveAll(encounter => encounter.SpawnTime + encounter.ValidFor < tappableCutoffTime);

        await SendSpawnedTappables(tappables, encounters);
    }

    private async Task DoSpawnCycle()
    {
        var activeTiles = _activeTiles.GetActiveTiles(_spawnCycleTime);

        while (_spawnCycleTime < U.CurrentTimeMillis())
        {
            _spawnCycleTime += SPAWN_INTERVAL;
            _spawnCycleIndex++;
        }

        List<Tappable> tappables = [];
        List<Encounter> encounters = [];
        foreach (ActiveTiles.ActiveTile activeTile in activeTiles)
        {
            DoSpawnCyclesForTile(activeTile.TileX, activeTile.TileY, _spawnCycleTime, _spawnCycleIndex, tappables, encounters);
        }

        long tappableCutoffTime = _spawnCycleTime - SPAWN_INTERVAL;

        tappables.RemoveAll(tappable => tappable.SpawnTime + tappable.ValidFor < tappableCutoffTime);
        encounters.RemoveAll(encounter => encounter.SpawnTime + encounter.ValidFor < tappableCutoffTime);

        await SendSpawnedTappables(tappables, encounters);
    }

    private void DoSpawnCyclesForTile(int tileX, int tileY, long spawnCycleTime, int spawnCycleIndex, List<Tappable> tappables, List<Encounter> encounters)
    {
        if (!_tileClassificationCache.TryGet(tileX, tileY, out TileClassification? tileClassification))
        {
            // still fetching the tile, the missed spawn cycles are caught up on once it has been fetched
            return;
        }

        int lastSpawnCycle = _lastSpawnCycleForTile.GetValueOrDefault((tileX << 16) + tileY);
        int cyclesToSpawn = Math.Min(spawnCycleIndex - lastSpawnCycle, _maxTappableLifetimeIntervals);
        for (int index = 0; index < cyclesToSpawn; index++)
        {
            SpawnTappablesForTile(tileX, tileY, tileClassification, spawnCycleTime - SPAWN_INTERVAL * (cyclesToSpawn - index - 1), tappables, encounters);
        }

        _lastSpawnCycleForTile[(tileX << 16) + tileY] = spawnCycleIndex;
    }

    private void SpawnTappablesForTile(int tileX, int tileY, TileClassification? tileClassification, long currentTime, List<Tappable> tappables, List<Encounter> encounters)
    {
        tappables.AddRange(_tappableGenerator.GenerateTappables(tileX, tileY, tileClassification, currentTime));
        encounters.AddRange(_encounterGenerator.GenerateEncounters(tileX, tileY, tileClassification, currentTime));
    }

    private async Task SendSpawnedTappables(List<Tappable> tappables, List<Encounter> encounters)
    {
        if (!await _publisher.PublishAsync("tappables", "tappableSpawn", Json.Serialize(tappables)))
        {
            Log.Error("Event bus server rejected tappable spawn event");
        }

        if (!await _publisher.PublishAsync("tappables", "encounterSpawn", Json.Serialize(encounters)))
        {
            Log.Error("Event bus server rejected encounter spawn event");
        }
    }
}
