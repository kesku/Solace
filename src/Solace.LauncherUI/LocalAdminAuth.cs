using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Options;

namespace Solace.LauncherUI;

// The launcher UI is reachable only from the LAN and tailnet, like the other
// homelab dashboards. Every request is treated as the local owner with all
// permissions, so no launcher login is needed. The game API on :8080 keeps its
// own authentication and OnlyAllowLocalLogin setting.
internal static class LocalAdmin
{
    public const string SchemeName = "LocalAdmin";

    public static ClaimsPrincipal Principal { get; } = Build();

    private static ClaimsPrincipal Build()
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, "local-owner"),
            new(ClaimTypes.NameIdentifier, "local-owner"),
        };
        claims.AddRange(Permissions.All.Select(p => new Claim("Permission", p)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName, ClaimTypes.Name, ClaimTypes.Role));
    }
}

internal sealed class LocalAdminAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var ticket = new AuthenticationTicket(LocalAdmin.Principal, LocalAdmin.SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

internal sealed class LocalAdminStateProvider : AuthenticationStateProvider
{
    public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
        Task.FromResult(new AuthenticationState(LocalAdmin.Principal));
}
