using Microsoft.EntityFrameworkCore;
using Solace.Common.Utils;
using Solace.ApiServer.Controllers.EarthApi;
using Solace.DB;
using Solace.DB.Models;
using Solace.DB.Models.Player;
using Solace.DB.Utils;
using Solace.ObjectStore.Client;

namespace Solace.ApiServer.Utils;

public static class TokenUtils
{
    // The tutorial ("oobe.*") steps new accounts start with, in the order the client registers them: permissions screen,
    // [tappable ftue], fake tappables, crystal activation, adventure, challenges, map location permission, journal, freedom (oobe.complete).
    // The client only knows about a step through its token, players who finished (or never got) the tutorial have none of them,
    // so a missing step is treated as done (inferred from that, not verified).
    // Left out, because Solace can't back them:
    // - oobe.adventure.* (starter chest, crystal activation, the scripted AR adventure): there are no crystal-activated player adventures
    //   (adventures/scrolls), and the AR step disables the back button until the script finishes
    // - oobe.challenges(.visible): waits for a completed tutorial challenge, which nothing would ever produce
    // - oobe.fake_map / oobe.fake_tappable: the fake map only goes away once the map permission step runs, whose gating isn't known well
    //   enough to rely on
    // - oobe.map_permissions / oobe.journal / oobe.complete: tested on the iOS client, the journal walkthrough opens an empty journal
    //   (the official flow gave an item from the fake tappable first) that stays on "Updating journal" with no way out until a restart,
    //   and the client never redeems these three, so after a restart they're just ignored
    private static readonly TokensEF.TutorialToken.StepE[] NewPlayerTutorialSteps =
    [
        TokensEF.TutorialToken.StepE.PERMISSIONS,
    ];

    // only for accounts that are being created, existing players must not be put through the tutorial again
    public static void AddNewPlayerTokens(Account account)
    {
        TokensEF tokens = account.Tokens!;
        foreach (TokensEF.TutorialToken.StepE step in NewPlayerTutorialSteps)
        {
            tokens.AddToken(Guid.NewGuid().ToString(), new TokensEF.TutorialToken(step));
        }
    }

    public static async Task<TokensEF.Token?> RedeemTokenAsync(
        EarthDbContext.Results results,
        Guid accountId,
        string tokenId,
        long currentTime,
        StaticData.StaticData staticData,
        ObjectStoreClient objectStore,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var transaction = await results.EarthDb.Database.BeginTransactionAsync(cancellationToken);
            var tokens = await results.EarthDb.Tokens
                .AsTracking()
                .FirstOrNewAsync(tokens => tokens.Id == accountId, trackNew: false, cancellationToken: cancellationToken);
            if (!tokens.Tokens.TryGetValue(tokenId, out TokensEF.Token? token) ||
                token is TokensEF.ChallengeProgressToken or TokensEF.DailyLoginToken ||
                GetExpirationTime(token) <= currentTime)
            {
                return null;
            }

            TokensEF.Token removedToken = tokens.RemoveToken(tokenId)!;

            await results.EarthDb.SaveChangesAsync(cancellationToken);
            results.Tokens = tokens.Version;
            await DoActionsOnRedeemedTokenAsync(results, removedToken, accountId, currentTime, staticData, objectStore);
            await transaction.CommitAsync(cancellationToken);
            return removedToken;
        }
        catch (DbUpdateConcurrencyException)
        {
            return null;
        }
    }

    public static async Task<string> AddTokenAsync(EarthDbContext.Results results, Guid accountId, TokensEF.Token token)
    {
        var tokens = await results.EarthDb.Tokens
            .AsTracking()
            .FirstOrNewAsync(tokens => tokens.Id == accountId);

        string id = Guid.NewGuid().ToString();
        tokens.AddToken(id, token);

        await results.EarthDb.SaveChangesAsync();

        results.Tokens = tokens.Version;

        return id;
    }

    // tokens for daily challenges are only valid until the challenges reset
    public static long? GetExpirationTime(TokensEF.Token token)
        => token switch
        {
            TokensEF.ChallengeCompletedToken challengeCompleted => challengeCompleted.ExpirationTime,
            TokensEF.ChallengeProgressedToken challengeProgressed => challengeProgressed.ExpirationTime,
            _ => null,
        };

    public static bool RemoveExpiredTokens(TokensEF tokens, long currentTime)
    {
        bool removed = false;
        foreach (TokensEF.TokenWithId token in tokens.GetTokens())
        {
            if (GetExpirationTime(token.Token) <= currentTime)
            {
                tokens.RemoveToken(token.Id);
                removed = true;
            }
        }

        return removed;
    }

    // does not handle redeeming the token itself (removing it from the list of tokens belonging to the player)
    public static async Task<TokensEF.Token> DoActionsOnRedeemedTokenAsync(EarthDbContext.Results results, TokensEF.Token token, Guid accountId, long currentTime, StaticData.StaticData staticData, ObjectStoreClient objectStore)
    {
        switch (token)
        {
            case TokensEF.LevelUpToken levelUpToken:
                {
                    await ActivityLogUtils.AddEntryAsync(results, accountId, new ActivityLogEF.LevelUpEntry(currentTime, levelUpToken.Level));

                    await Rewards.FromDBRewardsModel(levelUpToken.Rewards).ToRedeemQueryAsync(results, accountId, currentTime, staticData, objectStore);
                }

                break;
            case TokensEF.JournalItemUnlockedToken journalItemUnlockedToken:
                {
                    await ActivityLogUtils.AddEntryAsync(results, accountId, new ActivityLogEF.JournalItemUnlockedEntry(currentTime, journalItemUnlockedToken.ItemId));

                    /*int experiencePoints = staticData.catalog.itemsCatalog.getItem(journalItemUnlockedToken.itemId).experience().journal();
                    if (experiencePoints > 0)
                    {
                        updateQuery.then(new Rewards().addExperiencePoints(experiencePoints).toRedeemQuery(playerId, currentTime, staticData));
                    }*/
                }

                break;
            case TokensEF.ChallengeCompletedToken challengeCompletedToken:
                {
                    // the challenge stays listed as completed until it's claimed, by redeeming this token or through modifyState
                    if (challengeCompletedToken.ExpirationTime > currentTime)
                    {
                        var tokens = await results.EarthDb.Tokens
                            .AsTracking()
                            .FirstOrNewAsync(tokens => tokens.Id == accountId);
                        TokensEF.ChallengeProgressToken stored = tokens.Tokens.TryGetValue(ChallengesController.ProgressTokenId, out TokensEF.Token? raw) &&
                            raw is TokensEF.ChallengeProgressToken progressToken
                            ? progressToken
                            : new TokensEF.ChallengeProgressToken();
                        var progress = ChallengeProgressVersion.FromToken(stored);
                        progress.EnsureDate(currentTime);
                        progress.ClaimedChallengeIds.Add(challengeCompletedToken.ChallengeId);
                        progress.UpdatedAt = currentTime;
                        tokens.AddToken(ChallengesController.ProgressTokenId, progress.ToToken());
                        await results.EarthDb.SaveChangesAsync();
                        results.Tokens = tokens.Version;
                        results.Challenges = (int)(currentTime / 1000);
                    }

                    await Rewards.FromDBRewardsModel(challengeCompletedToken.Rewards).ToRedeemQueryAsync(results, accountId, currentTime, staticData, objectStore);
                }

                break;
        }

        return token;
    }
}
