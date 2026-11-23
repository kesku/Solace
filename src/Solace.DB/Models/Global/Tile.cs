using Solace.Common.Utils;

namespace Solace.DB.Models.Global;

public sealed class Tile : IEntityWithId<ulong>, IMergeable<Tile>
{
    public ulong Id { get; set; }

    public required string ObjectStoreId { get; set; }

    /// <summary>
    /// Unix time (seconds) of the last time this tile was served, used for LRU eviction.
    /// </summary>
    /// <remarks>Updated in batches, so it may lag behind the actual last access by a few minutes.</remarks>
    public long LastAccessed { get; set; }

    public async Task MergeWith(Tile other, ValueMerger merger)
    {
        merger.CurrentUserId = null;
        merger.CurrentUsername = null;

        if (ObjectStoreId != other.ObjectStoreId)
        {
            ObjectStoreId = other.ObjectStoreId;
        }

        if (LastAccessed < other.LastAccessed)
        {
            LastAccessed = other.LastAccessed;
        }
    }
}