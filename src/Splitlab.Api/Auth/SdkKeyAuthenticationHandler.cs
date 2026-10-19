using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Splitlab.Application;
using Splitlab.Domain.Projects;

namespace Splitlab.Api.Auth;

/// <summary>
/// Authenticates an SDK key presented as <c>Authorization: Bearer sl_...</c>. Anything that is not shaped like
/// one of our keys is left to other schemes; a well-formed key that does not verify is rejected.
/// A verified key scopes the request to its project, so it can never read another tenant's data (ADR-0010).
/// </summary>
internal sealed class SdkKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IAccessRepository access,
    ITenantContext tenant)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string header = Request.Headers.Authorization.ToString();
        const string bearer = "Bearer ";
        if (!header.StartsWith(bearer, StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        string presented = header[bearer.Length..].Trim();
        if (!ApiKey.TryGetPrefix(presented, out string prefix))
        {
            return AuthenticateResult.NoResult();
        }

        ApiKey? key = await access.FindApiKeyByPrefixAsync(prefix, Context.RequestAborted);
        if (key is null || !key.Matches(presented))
        {
            return AuthenticateResult.Fail("Invalid or revoked API key.");
        }

        var claims = new List<Claim>
        {
            new(SdkClaims.KeyPrefix, key.Prefix),
            new(SdkClaims.Environment, key.EnvironmentId.ToString()),
        };
        if (key.Permissions.HasFlag(ApiKeyPermissions.ConfigRead))
        {
            claims.Add(new Claim(SdkClaims.Permission, SdkClaims.ConfigRead));
        }

        if (key.Permissions.HasFlag(ApiKeyPermissions.EventsWrite))
        {
            claims.Add(new Claim(SdkClaims.Permission, SdkClaims.EventsWrite));
        }

        tenant.Use(key.ProjectId);
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }
}
