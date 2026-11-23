using Solace.Common.Utils;

namespace Solace.TappablesGenerator;

public static class SpawnLocation
{
    private static readonly int MAX_ATTEMPTS = 16;

    // Picks a random location in the tile, avoiding water, buildings, major roads and restricted areas if the tile classification is known.
    // tileBounds is north lat, south lat, west lon, east lon. Returns false if no suitable location was found (e.g. the tile is mostly water).
    // areaType is null if the tile classification is not known.
    public static bool TryPick(Random random, int tileX, int tileY, ReadOnlySpan<float> tileBounds, TileClassification? tileClassification, out float lat, out float lon, out AreaType? areaType)
    {
        for (int attempt = 0; attempt < MAX_ATTEMPTS; attempt++)
        {
            lat = random.NextSingle(tileBounds[1], tileBounds[0]);
            lon = random.NextSingle(tileBounds[2], tileBounds[3]);

            if (tileClassification is null)
            {
                areaType = null;
                return true;
            }

            AreaType pointAreaType = tileClassification.GetAreaType(tileX, tileY, lat, lon);
            if (TileClassification.IsSpawnable(pointAreaType))
            {
                areaType = pointAreaType;
                return true;
            }
        }

        lat = 0;
        lon = 0;
        areaType = null;
        return false;
    }
}
