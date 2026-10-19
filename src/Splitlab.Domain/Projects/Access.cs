using System.Security.Cryptography;
using System.Text;

namespace Splitlab.Domain.Projects;

/// <summary>Implemented by every entity that belongs to a project, so isolation can be enforced uniformly (ADR-0010).</summary>
public interface ITenantOwned
{
    Guid ProjectId { get; }
}

/// <summary>A person's role in one project. Higher values include the rights of lower ones.</summary>
public enum ProjectRole
{
    Viewer = 1,
    Editor = 2,
    Admin = 3,
}

public sealed class ProjectMember : ITenantOwned
{
    private ProjectMember()
    {
    }

    public Guid Id { get; private set; }

    public Guid ProjectId { get; private set; }

    /// <summary>The identity provider's <c>sub</c> claim.</summary>
    public string Subject { get; private set; } = "";

    public ProjectRole Role { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static ProjectMember Create(Guid projectId, string? subject, ProjectRole role, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(subject) || subject.Length > 200)
        {
            throw new DomainException("Member subject is required and must be at most 200 characters.");
        }

        if (!Enum.IsDefined(role))
        {
            throw new DomainException("Unknown role.");
        }

        return new ProjectMember { Id = Guid.NewGuid(), ProjectId = projectId, Subject = subject, Role = role, CreatedAt = now };
    }

    public void ChangeRole(ProjectRole role)
    {
        if (!Enum.IsDefined(role))
        {
            throw new DomainException("Unknown role.");
        }

        Role = role;
    }
}

/// <summary>What an SDK key is allowed to do.</summary>
[Flags]
public enum ApiKeyPermissions
{
    None = 0,
    ConfigRead = 1,
    EventsWrite = 2,
}

/// <summary>
/// A machine credential scoped to one environment. The secret is shown once at creation;
/// only its SHA-256 hash and a short non-secret prefix are stored (ADR-0010). Keys carry
/// 256 bits of randomness, so a fast hash is appropriate (unlike passwords).
/// </summary>
public sealed class ApiKey : ITenantOwned
{
    public const string TokenPrefix = "sl_";

    private const int PrefixHexLength = 12;

    private ApiKey()
    {
    }

    public Guid Id { get; private set; }

    public Guid ProjectId { get; private set; }

    public Guid EnvironmentId { get; private set; }

    public string Name { get; private set; } = "";

    /// <summary>Non-secret identifier, safe to show in lists and logs.</summary>
    public string Prefix { get; private set; } = "";

    public string KeyHash { get; private set; } = "";

    public ApiKeyPermissions Permissions { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public bool IsActive => RevokedAt is null;

    /// <summary>Creates a key and returns the one-time secret to hand to the caller.</summary>
    public static (ApiKey Key, string Secret) Issue(
        Guid projectId, Guid environmentId, string? name, ApiKeyPermissions permissions, DateTimeOffset now)
    {
        if (permissions == ApiKeyPermissions.None || (permissions & ~(ApiKeyPermissions.ConfigRead | ApiKeyPermissions.EventsWrite)) != 0)
        {
            throw new DomainException("An SDK key needs at least one valid permission.");
        }

        string prefix = Convert.ToHexString(RandomNumberGenerator.GetBytes(PrefixHexLength / 2)).ToLowerInvariant();
        string secretPart = Base64Url(RandomNumberGenerator.GetBytes(32));
        string secret = $"{TokenPrefix}{prefix}_{secretPart}";

        var key = new ApiKey
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            EnvironmentId = environmentId,
            Name = Keys.RequireName(name, "Key name"),
            Prefix = prefix,
            KeyHash = Hash(secret),
            Permissions = permissions,
            CreatedAt = now,
        };
        return (key, secret);
    }

    /// <summary>Extracts the lookup prefix from a presented secret, or false if it is not shaped like one of ours.</summary>
    public static bool TryGetPrefix(string? presented, out string prefix)
    {
        prefix = "";
        if (presented is null
            || !presented.StartsWith(TokenPrefix, StringComparison.Ordinal)
            || presented.Length <= TokenPrefix.Length + PrefixHexLength + 1
            || presented[TokenPrefix.Length + PrefixHexLength] != '_')
        {
            return false;
        }

        prefix = presented.Substring(TokenPrefix.Length, PrefixHexLength);
        return true;
    }

    /// <summary>Constant-time check of a presented secret against the stored hash. Revoked keys never match.</summary>
    public bool Matches(string presented) =>
        IsActive
        && CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(Hash(presented)), Encoding.ASCII.GetBytes(KeyHash));

    public void Revoke(DateTimeOffset now) => RevokedAt ??= now;

    private static string Hash(string secret) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
