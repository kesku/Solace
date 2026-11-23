using Microsoft.EntityFrameworkCore;
using Solace.Common.Utils;
using Solace.ApiServer.Controllers.EarthApi;
using Solace.DB;
using Solace.DB.Models.Player;
using Solace.DB.Utils;
using Solace.ObjectStore.Client;

namespace Solace.ApiServer.Utils;

public static class TokenUtils
{
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
                token is TokensEF.ChallengeProgressToken or TokensEF.DailyLoginToken)
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
