using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Splitlab.Application;
using Splitlab.Domain.Projects;

namespace Splitlab.Api.Auth;

public static class AuthSchemes
{
    /// <summary>People: JWT bearer tokens from an OpenID Connect authority (or the local dev issuer).</summary>
    public const string User = JwtBearerDefaults.AuthenticationScheme;

    /// <summary>Applications: environment-scoped SDK keys.</summary>
    public const string SdkKey = "SdkKey";
}

public static class Policies
{
    public const string SignedIn = "SignedIn";
    public const string Viewer = "Viewer";
    public const string Editor = "Editor";
    public const string Admin = "Admin";
    public const string ConfigRead = "ConfigRead";
}

/// <summary>Names of the claims the SDK key scheme issues.</summary>
public static class SdkClaims
{
    public const string KeyPrefix = "sl_key";
    public const string Environment = "sl_env";
    public const string Permission = "sl_perm";
    public const string ConfigRead = "config:read";
    public const string EventsWrite = "events:write";
}

public static class AuthSetup
{
    public const string LocalIssuer = "splitlab-local";
    public const string LocalAudience = "splitlab";

    /// <summary>
    /// Configures people (JWT) and machine (SDK key) authentication. Set <c>Auth:Authority</c> and <c>Auth:Audience</c>
    /// for a real OpenID Connect provider, or <c>Auth:Local:SigningKey</c> for the built-in development issuer.
    /// </summary>
    public static IServiceCollection AddSplitlabAuth(this IServiceCollection services, IConfiguration config)
    {
        string? authority = config["Auth:Authority"];
        string? localKey = config["Auth:Local:SigningKey"];

        if (string.IsNullOrWhiteSpace(authority) && string.IsNullOrWhiteSpace(localKey))
        {
            throw new InvalidOperationException(
                "Authentication is not configured. Set Auth:Authority (and Auth:Audience), or Auth:Local:SigningKey for development.");
        }

        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, HttpCurrentUser>();

        services.AddAuthentication(AuthSchemes.User)
            .AddJwtBearer(AuthSchemes.User, options =>
            {
                options.MapInboundClaims = false;
                if (!string.IsNullOrWhiteSpace(authority))
                {
                    options.Authority = authority;
                    options.Audience = config["Auth:Audience"];
                }
                else
                {
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidIssuer = LocalIssuer,
                        ValidAudience = LocalAudience,
                        IssuerSigningKey = LocalSigningKey(localKey!),
                    };
                }
            })
            .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, SdkKeyAuthenticationHandler>(
                AuthSchemes.SdkKey, null);

        services.AddScoped<IAuthorizationHandler, ProjectRoleHandler>();
        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.SignedIn, p => p.AddAuthenticationSchemes(AuthSchemes.User).RequireAuthenticatedUser())
            .AddPolicy(Policies.Viewer, p => ProjectRolePolicy(p, ProjectRole.Viewer))
            .AddPolicy(Policies.Editor, p => ProjectRolePolicy(p, ProjectRole.Editor))
            .AddPolicy(Policies.Admin, p => ProjectRolePolicy(p, ProjectRole.Admin))
            .AddPolicy(Policies.ConfigRead, p => p
                .AddAuthenticationSchemes(AuthSchemes.SdkKey)
                .RequireAuthenticatedUser()
                .RequireClaim(SdkClaims.Permission, SdkClaims.ConfigRead));

        return services;
    }

    public static SymmetricSecurityKey LocalSigningKey(string key) => new(Encoding.UTF8.GetBytes(key));

    /// <summary>Mints a token for the local development issuer. Only reachable when no external authority is configured.</summary>
    public static string MintLocalToken(string signingKey, string subject, TimeSpan lifetime) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = LocalIssuer,
            Audience = LocalAudience,
            Expires = DateTime.UtcNow.Add(lifetime),
            Claims = new Dictionary<string, object> { ["sub"] = subject },
            SigningCredentials = new SigningCredentials(LocalSigningKey(signingKey), SecurityAlgorithms.HmacSha256),
        });

    private static void ProjectRolePolicy(AuthorizationPolicyBuilder p, ProjectRole minimum) =>
        p.AddAuthenticationSchemes(AuthSchemes.User)
            .RequireAuthenticatedUser()
            .AddRequirements(new ProjectRoleRequirement(minimum));
}
