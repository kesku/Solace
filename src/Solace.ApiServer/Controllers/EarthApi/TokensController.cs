using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using Solace.ApiServer.Exceptions;
using Solace.ApiServer.Types.Common;
using Solace.ApiServer.Utils;
using Solace.Common.Utils;
using Solace.DB;
using Solace.DB.Models.Player;
using Rewards = Solace.ApiServer.Utils.Rewards;
using Microsoft.EntityFrameworkCore;
using Solace.DB.Utils;
using Solace.ObjectStore.Client;
using System.Diagnostics;
using System.Globalization;

namespace Solace.ApiServer.Controllers.EarthApi;

[Authorize]
[ApiVersion("1.1")]
[Route("1/api/v{version:apiVersion}/player/tokens")]
internal sealed class TokensController : SolaceControllerBase
{
    private readonly EarthDbContext _earthDb;
    private readonly StaticData.StaticData _staticData;
    private readonly ObjectStoreClient _objectStore;

    public TokensController(EarthDbContext earthDb, StaticData.StaticData staticData, ObjectStoreClient objectStore)
    {
        _earthDb = earthDb;
        _staticData = staticData;
        _objectStore = objectStore;
    }

    [HttpGet]
    public async Task<Results<ContentHttpResult, BadRequest>> Get(CancellationToken cancellationToken)
    {
        if (!TryGetAccountId(out var accountId))
        {
            return TypedResults.BadRequest();
        }

        var tokens = await _earthDb.Tokens
            .AsNoTracking()
            .FirstOrNewAsync(tokens => tokens.Id == accountId, trackNew: false, cancellationToken: cancellationToken);

        return EarthJson(new Dictionary<string, Dictionary<string, Token>>()
        {
            { "tokens", TokensToApiResponse(tokens) }
        }, null);
    }

    [HttpPost("{tokenId}/redeem")]
    public async Task<Results<ContentHttpResult, BadRequest>> Redeem(string tokenId, CancellationToken cancellationToken)
    {
        if (!TryGetAccountId(out var accountId))
        {
            return TypedResults.BadRequest();
        }

        // request.timestamp
        long requestStartedOn = HttpContext.GetTimestamp();

        var removedToken = await TokenUtils.RedeemTokenAsync(
            new EarthDbContext.Results(_earthDb), accountId, tokenId, requestStartedOn, _staticData, _objectStore, cancellationToken);

        if (removedToken is not null)
        {
            return EarthJson(TokenToApiResponse(removedToken));
        }
        else
        {
            return TypedResults.BadRequest();
        }
    }

    internal static Dictionary<string, Token> TokensToApiResponse(TokensEF tokens)
        => tokens.GetTokens()
            .Where(token => token.Token is not TokensEF.ChallengeProgressToken and not TokensEF.DailyLoginToken)
            .Select(token => new KeyValuePair<string, Token>(token.Id, TokenToApiResponse(token.Token)))
            .ToDictionary();

    private static Token TokenToApiResponse(TokensEF.Token token)
    {
        Dictionary<string, string> properties = [];
        switch (token)
        {
            case TokensEF.JournalItemUnlockedToken journalItemUnlocked:
                properties["itemid"] = journalItemUnlocked.ItemId;
                break;
            case TokensEF.ChallengeCompletedToken challengeCompleted:
                properties["challengeid"] = challengeCompleted.ChallengeId;
                properties["category"] = challengeCompleted.Category;
                properties["expirationtimeutc"] = TimeFormatter.FormatTime(challengeCompleted.ExpirationTime);
                break;
            case TokensEF.ChallengeProgressedToken challengeProgressed:
                properties["challengeid"] = challengeProgressed.ChallengeId;
                properties["challengereferenceid"] = challengeProgressed.ChallengeReferenceId;
                properties["percentcomplete"] = (challengeProgressed.CurrentCount * 100 / Math.Max(1, challengeProgressed.TotalThreshold)).ToString(CultureInfo.InvariantCulture);
                properties["currentcount"] = challengeProgressed.CurrentCount.ToString(CultureInfo.InvariantCulture);
                properties["previouscount"] = challengeProgressed.PreviousCount.ToString(CultureInfo.InvariantCulture);
                properties["totalthreshold"] = challengeProgressed.TotalThreshold.ToString(CultureInfo.InvariantCulture);
                break;
        }

        Rewards rewards = token switch
        {
            TokensEF.LevelUpToken levelUp => Rewards.FromDBRewardsModel(levelUp.Rewards).SetLevel(levelUp.Level),
            TokensEF.ChallengeCompletedToken challengeCompleted => Rewards.FromDBRewardsModel(challengeCompleted.Rewards),
            _ => new Rewards(),
        };

        Token.LifetimeE lifetime = token switch
        {
            TokensEF.LevelUpToken => Token.LifetimeE.TRANSIENT,
            TokensEF.JournalItemUnlockedToken => Token.LifetimeE.PERSISTENT,
            TokensEF.ChallengeCompletedToken => Token.LifetimeE.PERSISTENT,
            TokensEF.ChallengeProgressedToken => Token.LifetimeE.TRANSIENT, // guessed
            TokensEF.TutorialToken => Token.LifetimeE.PERSISTENT,
            _ => throw new InvalidDataException($"Unknown Token type '{token?.GetType()?.ToString() ?? null}'"),
        };

        return new Token(
            lifetime,
            token is TokensEF.TutorialToken tutorial ? Token.Type.FromDb(tutorial.Step) : Token.Type.FromDb(token.Type),
            properties,
            rewards.ToApiResponse()
        );
    }
}
