using Splitlab.Application;

namespace Splitlab.Api.Auth;

internal sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private System.Security.Claims.ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public string? Subject => Principal?.FindFirst("sub")?.Value;

    public string Actor =>
        Subject
        ?? (Principal?.FindFirst(SdkClaims.KeyPrefix)?.Value is { } prefix ? $"sdk-key:{prefix}" : "anonymous");
}
