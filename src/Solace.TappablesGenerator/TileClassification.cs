namespace Solace.TappablesGenerator;

// The area type of each pixel of a zoom 16 map tile, decoded from the tile rendered by Solace.TileRenderer
public sealed class TileClassification
{
    public const int ZOOM = 16;

    private readonly byte[] _areaTypes;

    public TileClassification(int width, int height, byte[] areaTypes)
    {
        if (width <= 0 || height <= 0 || areaTypes.Length != width * height)
        {
            throw new ArgumentException("Area type count does not match the tile size", nameof(areaTypes));
        }

        Width = width;
        Height = height;
        _areaTypes = areaTypes;
    }

    public int Width { get; }

    public int Height { get; }

    public static TileClassification FromPng(ReadOnlySpan<byte> png)
    {
        byte[] values = PngReader.ReadFirstChannel(png, out int width, out int height);

        // area types are multiples of 0x11, snap to the nearest one in case a value is slightly off
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = (byte)(int.Clamp((values[i] + 8) / 0x11, 0, 0xF) * 0x11);
        }

        return new TileClassification(width, height, values);
    }

    public AreaType GetAreaType(int tileX, int tileY, double lat, double lon)
    {
        (int x, int y) = LatLonToPixel(lat, lon, tileX, tileY, ZOOM, Width, Height);
        return (AreaType)_areaTypes[(y * Width) + x];
    }

    public static bool IsSpawnable(AreaType areaType)
        => areaType is not (AreaType.WATER or AreaType.BUILDING or AreaType.HIGHWAY_MAJOR or AreaType.RESTRICTED_AREA);

    // Web Mercator, same as the tile renderer: pixel (0, 0) is the north west corner of the tile, x increases to the east and y to the south.
    // Points outside of the tile are clamped to the nearest edge pixel.
    public static (int X, int Y) LatLonToPixel(double lat, double lon, int tileX, int tileY, int zoom, int width, int height)
    {
        double tileCount = 1 << zoom;
        double x = (lon + 180.0) / 360.0 * tileCount;
        double y = (1.0 - (double.Asinh(double.Tan(double.DegreesToRadians(lat))) / double.Pi)) / 2.0 * tileCount;

        int pixelX = (int)double.Floor((x - tileX) * width);
        int pixelY = (int)double.Floor((y - tileY) * height);

        return (int.Clamp(pixelX, 0, width - 1), int.Clamp(pixelY, 0, height - 1));
    }
}
