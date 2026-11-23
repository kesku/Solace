using Serilog;
using Solace.Common;
using Solace.Common.Utils;
using Solace.StaticData;

namespace Solace.TappablesGenerator;

public class TappableGenerator
{
    // TODO: make these configurable
    private static readonly int MIN_COUNT = 1;
    private static readonly int MAX_COUNT = 3;
    private static readonly long MIN_DURATION = 2 * 60 * 1000;
    private static readonly long MAX_DURATION = 5 * 60 * 1000;
    private static readonly long MIN_DELAY = 1 * 60 * 1000;
    private static readonly long MAX_DELAY = 2 * 60 * 1000;

    // Tappables whose icon (without the namespace) starts with the prefix are this many times as likely to be picked on the area type
    private static readonly (string IconPrefix, AreaType AreaType, int Weight)[] AREA_TYPE_WEIGHTS =
    [
        ("tree_", AreaType.FOREST, 4),
        ("tree_", AreaType.OPEN_PUBLIC_AREA, 2),
    ];

    private readonly StaticData.StaticData _staticData;

    // weight of each tappable config (same order as TappablesConfig.Tappables) for area types that don't use uniform weights
    private readonly Dictionary<AreaType, int[]> _areaTypeWeights = [];

    private readonly Random _random;

    public TappableGenerator(StaticData.StaticData staticData)
    {
        _staticData = staticData;

        if (_staticData.TappablesConfig.Tappables.Length == 0)
        {
            Log.Warning("No tappable configs provided");
        }

        foreach (var (iconPrefix, areaType, weight) in AREA_TYPE_WEIGHTS)
        {
            for (int i = 0; i < _staticData.TappablesConfig.Tappables.Length; i++)
            {
                string icon = _staticData.TappablesConfig.Tappables[i].Icon;
                if (icon[(icon.IndexOf(':', StringComparison.Ordinal) + 1)..].StartsWith(iconPrefix, StringComparison.Ordinal))
                {
                    if (!_areaTypeWeights.TryGetValue(areaType, out int[]? weights))
                    {
                        weights = new int[_staticData.TappablesConfig.Tappables.Length];
                        Array.Fill(weights, 1);
                        _areaTypeWeights[areaType] = weights;
                    }

                    weights[i] = weight;
                }
            }
        }

        _random = new Random();
    }

    public static long GetMaxTappableLifetime()
        => MAX_DELAY + MAX_DURATION + 30 * 1000;

    public IEnumerable<Tappable> GenerateTappables(int tileX, int tileY, TileClassification? tileClassification, long currentTime)
    {
        if (_staticData.TappablesConfig.Tappables.Length == 0)
        {
            return [];
        }

        int count = _random.Next(MIN_COUNT, MAX_COUNT + 1);

        var tappables = new List<Tappable>(count);
        Span<float> tileBounds = stackalloc float[4];
        for (; count > 0; count--)
        {
            long spawnDelay = _random.NextInt64(MIN_DELAY, MAX_DELAY + 1);
            long duration = _random.NextInt64(MIN_DURATION, MAX_DURATION + 1);

            GetTileBounds(tileX, tileY, tileBounds);
            if (!SpawnLocation.TryPick(_random, tileX, tileY, tileBounds, tileClassification, out float lat, out float lon, out AreaType? areaType))
            {
                // no suitable location found (e.g. the tile is mostly water)
                continue;
            }

            TappablesConfig.TappableConfig tappableConfig = _staticData.TappablesConfig.Tappables[PickTappableConfigIndex(areaType)];

            int dropSetIndex = _random.Next(0, tappableConfig.DropSets.Select(dropSet => dropSet.Chance).Sum());
            TappablesConfig.TappableConfig.DropSetR? dropSet = null;

            foreach (TappablesConfig.TappableConfig.DropSetR dropSet1 in tappableConfig.DropSets)
            {
                dropSet = dropSet1;
                dropSetIndex -= dropSet1.Chance;
                if (dropSetIndex <= 0)
                {
                    break;
                }
            }

            if (dropSet is null)
            {
                throw new InvalidOperationException();
            }

            var items = new List<Tappable.Item>(dropSet.Items.Length);

            foreach (string itemId in dropSet.Items)
            {
                TappablesConfig.TappableConfig.ItemCount itemCount = tappableConfig.ItemCounts[itemId];
                items.Add(new Tappable.Item(itemId, _random.Next(itemCount.Min, itemCount.Max + 1)));
            }

            var rarity = Tappable.RarityE.FromStaticData(items.Max(item => _staticData.Catalog.ItemsCatalog.GetItem(item.Id)!.Rarity));

            var tappable = new Tappable(
                Guid.CreateVersion7(),
                lat,
                lon,
                currentTime + spawnDelay,
                duration,
                tappableConfig.Icon,
                rarity,
                [.. items]
            );

            tappables.Add(tappable);
        }

        return tappables;
    }

    private int PickTappableConfigIndex(AreaType? areaType)
    {
        if (areaType is null || !_areaTypeWeights.TryGetValue(areaType.Value, out int[]? weights))
        {
            return _random.Next(0, _staticData.TappablesConfig.Tappables.Length);
        }

        int index = _random.Next(0, weights.Sum());
        for (int i = 0; i < weights.Length; i++)
        {
            index -= weights[i];
            if (index < 0)
            {
                return i;
            }
        }

        return weights.Length - 1;
    }

    private static void GetTileBounds(int tileX, int tileY, Span<float> dest)
    {
        dest[0] = YToLat((float)tileY / (1 << 16));
        dest[1] = YToLat((float)(tileY + 1) / (1 << 16));
        dest[2] = XToLon((float)tileX / (1 << 16));
        dest[3] = XToLon((float)(tileX + 1) / (1 << 16));
    }

    private static float XToLon(float x)
        => (float)MathE.ToDegrees((x * 2.0d - 1.0d) * double.Pi);

    private static float YToLat(float y)
        => (float)MathE.ToDegrees(double.Atan(double.Sinh((1.0d - y * 2.0d) * double.Pi)));
}