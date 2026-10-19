using Splitlab.Domain;
using Splitlab.Domain.Projects;

namespace Splitlab.Domain.Tests;

public class AccessTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);

    private static (ApiKey Key, string Secret) NewKey(ApiKeyPermissions permissions = ApiKeyPermissions.ConfigRead) =>
        ApiKey.Issue(Guid.NewGuid(), Guid.NewGuid(), "server", permissions, Now);

    [Fact]
    public void SecretHasTheExpectedShapeAndPrefixCanBeRecovered()
    {
        (ApiKey key, string secret) = NewKey();

        Assert.StartsWith("sl_", secret, StringComparison.Ordinal);
        Assert.True(ApiKey.TryGetPrefix(secret, out string prefix));
        Assert.Equal(key.Prefix, prefix);
        Assert.Contains(prefix, secret, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyTheHashIsKept()
    {
        (ApiKey key, string secret) = NewKey();

        Assert.DoesNotContain(secret, key.KeyHash, StringComparison.Ordinal);
        Assert.Equal(64, key.KeyHash.Length);
    }

    [Fact]
    public void MatchesTheRightSecretOnly()
    {
        (ApiKey key, string secret) = NewKey();
        (_, string other) = NewKey();

        Assert.True(key.Matches(secret));
        Assert.False(key.Matches(other));
        Assert.False(key.Matches(secret + "x"));
        Assert.False(key.Matches(""));
    }

    [Fact]
    public void RevokedKeysNeverMatchAndRevokingIsIdempotent()
    {
        (ApiKey key, string secret) = NewKey();

        key.Revoke(Now);
        key.Revoke(Now.AddDays(1));

        Assert.False(key.IsActive);
        Assert.False(key.Matches(secret));
        Assert.Equal(Now, key.RevokedAt);
    }

    [Fact]
    public void KeysAreUnique()
    {
        var secrets = Enumerable.Range(0, 50).Select(_ => NewKey().Secret).ToHashSet();

        Assert.Equal(50, secrets.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sl_")]
    [InlineData("sl_abc_def")]
    [InlineData("xx_0123456789ab_secret")]
    [InlineData("sl_0123456789abXsecret")]
    public void MalformedSecretsHaveNoPrefix(string? presented)
    {
        Assert.False(ApiKey.TryGetPrefix(presented, out _));
    }

    [Fact]
    public void AKeyNeedsAtLeastOneKnownPermission()
    {
        Assert.Throws<DomainException>(() => NewKey(ApiKeyPermissions.None));
        Assert.Throws<DomainException>(() => NewKey((ApiKeyPermissions)128));
        _ = NewKey(ApiKeyPermissions.ConfigRead | ApiKeyPermissions.EventsWrite);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public void MembersNeedASubject(string? subject)
    {
        Assert.Throws<DomainException>(() => ProjectMember.Create(Guid.NewGuid(), subject, ProjectRole.Viewer, Now));
    }

    [Fact]
    public void RolesAreOrderedByPrivilege()
    {
        Assert.True(ProjectRole.Admin > ProjectRole.Editor);
        Assert.True(ProjectRole.Editor > ProjectRole.Viewer);
        Assert.Throws<DomainException>(() => ProjectMember.Create(Guid.NewGuid(), "u", (ProjectRole)0, Now));
    }
}
