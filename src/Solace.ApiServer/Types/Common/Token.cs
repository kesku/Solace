
using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Solace.ApiServer.Types.Common;

// property order matches captured responses from the official server
public sealed record Token(
    Token.LifetimeE Lifetime,
    Token.Type ClientType,
    Dictionary<string, string> ClientProperties,
    Rewards Rewards
)
{
    // The client only keeps FNV-1a-32 hashes of the client types it understands (a table of ~40 entries
    // in the 0.33 iOS binary), any other client type is ignored. Every name below has a matching hash in that table,
    // except for redeemtappable, which is only used for the token embedded in the tappable redeem response.
    // sunset.overlay (the end-of-service screen) is in the table too, but must never be sent.
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum Type
    {
#pragma warning disable CA1707 // Identifiers should not contain underscores
        // this used to be sent as "adv_zyki", which happens to have the same hash
        [JsonStringEnumMemberName("playerlevel")]
        LEVEL_UP,
        [JsonStringEnumMemberName("player.level.rewards.reconciled")]
        LEVEL_REWARDS_RECONCILED,
        [JsonStringEnumMemberName("redeemtappable")]
        TAPPABLE,
        [JsonStringEnumMemberName("item.unlocked")]
        JOURNAL_ITEM_UNLOCKED,
        [JsonStringEnumMemberName("item.unlocked.notification")]
        JOURNAL_ITEM_UNLOCKED_NOTIFICATION,
        [JsonStringEnumMemberName("challenge.completed")]
        CHALLENGE_COMPLETED,
        [JsonStringEnumMemberName("challenge.progress")]
        CHALLENGE_PROGRESS,
        [JsonStringEnumMemberName("seasons.welcome")]
        SEASONS_WELCOME,
        [JsonStringEnumMemberName("seasons.notification")]
        SEASONS_NOTIFICATION,
        [JsonStringEnumMemberName("empty.bucket.returned")]
        EMPTY_BUCKET_RETURNED,
        [JsonStringEnumMemberName("adventure.result.notification")]
        ADVENTURE_RESULT_NOTIFICATION,
        [JsonStringEnumMemberName("ftue.context.adventures")]
        FTUE_CONTEXT_ADVENTURES,
        [JsonStringEnumMemberName("ftue.placement.adventures.ar")]
        FTUE_PLACEMENT_ADVENTURES_AR,
        [JsonStringEnumMemberName("ftue.crystal.activation.completed")]
        FTUE_CRYSTAL_ACTIVATION_COMPLETED,
        [JsonStringEnumMemberName("ftue.adventure.crystal.preview.completed")]
        FTUE_ADVENTURE_CRYSTAL_PREVIEW_COMPLETED,
        [JsonStringEnumMemberName("oobe.permissions")]
        OOBE_PERMISSIONS,
        [JsonStringEnumMemberName("oobe.map_permissions")]
        OOBE_MAP_PERMISSIONS,
        [JsonStringEnumMemberName("oobe.fake_map")]
        OOBE_FAKE_MAP,
        [JsonStringEnumMemberName("oobe.fake_tappable")]
        OOBE_FAKE_TAPPABLE,
        [JsonStringEnumMemberName("oobe.adventure.spawn_crystal")]
        OOBE_ADVENTURE_SPAWN_CRYSTAL,
        [JsonStringEnumMemberName("oobe.adventure.tappable_reward")]
        OOBE_ADVENTURE_TAPPABLE_REWARD,
        [JsonStringEnumMemberName("oobe.adventure.in_ar")]
        OOBE_ADVENTURE_IN_AR,
        [JsonStringEnumMemberName("oobe.challenges")]
        OOBE_CHALLENGES,
        [JsonStringEnumMemberName("oobe.challenges.visible")]
        OOBE_CHALLENGES_VISIBLE,
        [JsonStringEnumMemberName("oobe.journal")]
        OOBE_JOURNAL,
        [JsonStringEnumMemberName("oobe.complete")]
        OOBE_COMPLETE,
        [JsonStringEnumMemberName("game.eula")]
        GAME_EULA,
        [JsonStringEnumMemberName("migrated.to.playfab.entitlements")]
        MIGRATED_TO_PLAYFAB_ENTITLEMENTS
#pragma warning restore CA1707 // Identifiers should not contain underscores
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum LifetimeE
    {
        [JsonStringEnumMemberName("Persistent")]
        PERSISTENT,
        [JsonStringEnumMemberName("Transient")]
        TRANSIENT
    }
}

public static class TokenTypeExtensions
{
    extension(Token.Type)
    {
        public static Token.Type FromDb(DB.Models.Player.TokensEF.Token.TypeE type)
            => type switch
            {
                DB.Models.Player.TokensEF.Token.TypeE.LEVEL_UP => Token.Type.LEVEL_UP,
                DB.Models.Player.TokensEF.Token.TypeE.JOURNAL_ITEM_UNLOCKED => Token.Type.JOURNAL_ITEM_UNLOCKED,
                DB.Models.Player.TokensEF.Token.TypeE.CHALLENGE_COMPLETED => Token.Type.CHALLENGE_COMPLETED,
                DB.Models.Player.TokensEF.Token.TypeE.CHALLENGE_PROGRESSED => Token.Type.CHALLENGE_PROGRESS,
                _ => throw new InvalidEnumArgumentException(nameof(type), (int)type, typeof(DB.Models.Player.TokensEF.Token.TypeE)),
            };
    }
}
