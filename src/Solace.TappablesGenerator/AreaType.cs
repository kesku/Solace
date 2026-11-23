namespace Solace.TappablesGenerator;

// Grey level of each area type in the map tiles rendered by Solace.TileRenderer, must match Solace.TileRenderer.AreaType
public enum AreaType : byte
{
#pragma warning disable CA1707 // Identifiers should not contain underscores
    RESTRICTED_AREA = 0x00,
    HIGHWAY_MAJOR = 0x11,
    HIGHWAY_MINOR = 0x22,
    HIGHWAY_SERVICE = 0x33,
    CYCLE_PATH = 0x44,
    MOUNTAIN = 0x55,
    SAND = 0x66,
    PIER = 0x77,
    FOOTPATH = 0x88,
    WATER = 0x99,
    ATHLETIC_FIELD = 0xAA,
    OPEN_PRIVATE_AREA = 0xBB,
    OPEN_PUBLIC_AREA = 0xCC,
    FOREST = 0xDD,
    BUILDING = 0xEE,
    BASE_BACKGROUND = 0xFF,
#pragma warning restore CA1707 // Identifiers should not contain underscores
}
