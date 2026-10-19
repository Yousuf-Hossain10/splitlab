using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Splitlab.Api.Auth;
using Splitlab.Application;
using Splitlab.Domain.Experiments;
using Splitlab.Domain.Projects;
using Splitlab.Infrastructure.Persistence;

namespace Splitlab.Api.Tests;

/// <summary>Authentication, role enforcement and the guarantee that one project can never reach another's data (ADR-0010).</summary>
public sealed class SecurityTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly Guid SomeId = Guid.NewGuid();
    private static readonly string[] ConfigRead = ["config:read"];

    // ---- authentication ----

    [Theory]
    [InlineData("/v1/projects")]
    [InlineData("/v1/projects/anything")]
    [InlineData("/v1/projects/anything/environments")]
    [InlineData("/v1/config/anything")]
    public async Task AnonymousCallersAreRejected(string url)
    {
        HttpResponseMessage response = await factory.CreateClient().GetAsync(url);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TokensSignedWithAnotherKeyAreRejected()
    {
        HttpClient client = factory.CreateClient();
        string forged = AuthSetup.MintLocalToken("some-other-signing-key-some-other-key-0123", "mallory", TimeSpan.FromMinutes(5));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", forged);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/v1/projects")).StatusCode);
    }

    [Fact]
    public async Task ExpiredTokensAreRejected()
    {
        HttpClient client = factory.CreateClient();
        string expired = AuthSetup.MintLocalToken(ApiFactory.SigningKey, "alice", TimeSpan.FromMinutes(-10));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", expired);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/v1/projects")).StatusCode);
    }

    // ---- tenant isolation over HTTP ----

    [Fact]
    public async Task AnotherProjectsMemberCannotTouchAnyProjectEndpoint()
    {
        Workspace alice = await Workspace.CreateAsync(factory);
        await alice.CreateExperimentAsync();
        Workspace bob = await Workspace.CreateAsync(factory);
        string p = $"/v1/projects/{alice.Project}";
        string e = alice.EnvironmentUrl;

        var attempts = new (HttpMethod Method, string Url, object? Body)[]
        {
            (HttpMethod.Get, p, null),
            (HttpMethod.Get, $"{p}/environments", null),
            (HttpMethod.Post, $"{p}/environments", new { key = "stolen", name = "x" }),
            (HttpMethod.Get, $"{p}/members", null),
            (HttpMethod.Put, $"{p}/members/bob", new { role = "Admin" }),
            (HttpMethod.Delete, $"{p}/members/{alice.User}", null),
            (HttpMethod.Get, $"{e}/experiments", null),
            (HttpMethod.Get, $"{e}/experiments/checkout", null),
            (HttpMethod.Post, $"{e}/experiments", new { key = "x", name = "x", variations = new[] { new { key = "a", trafficBasisPoints = 1 } } }),
            (HttpMethod.Put, $"{e}/experiments/checkout/variations", new { version = 1, variations = new[] { new { key = "a", trafficBasisPoints = 1 } } }),
            (HttpMethod.Post, $"{e}/experiments/checkout/start", new { version = 1 }),
            (HttpMethod.Post, $"{e}/experiments/checkout/stop", new { version = 1 }),
            (HttpMethod.Get, $"{e}/api-keys", null),
            (HttpMethod.Post, $"{e}/api-keys", new { name = "k", permissions = new[] { "config:read" } }),
            (HttpMethod.Post, $"{e}/api-keys/{SomeId}/revoke", null),
            (HttpMethod.Post, $"{e}/api-keys/{SomeId}/rotate", null),
        };

        foreach ((HttpMethod method, string url, object? body) in attempts)
        {
            using var request = new HttpRequestMessage(method, url);
            if (body is not null)
            {
                request.Content = JsonContent.Create(body);
            }

            HttpResponseMessage response = await bob.Client.SendAsync(request);

            Assert.True(
                response.StatusCode == HttpStatusCode.Forbidden,
                $"{method} {url} returned {(int)response.StatusCode}, expected 403");
        }
    }

    [Fact]
    public async Task UnknownProjectsLookTheSameAsOthersProjects()
    {
        Workspace alice = await Workspace.CreateAsync(factory);
        Workspace bob = await Workspace.CreateAsync(factory);

        HttpStatusCode others = (await bob.Client.GetAsync($"/v1/projects/{alice.Project}")).StatusCode;
        HttpStatusCode missing = (await bob.Client.GetAsync("/v1/projects/does-not-exist")).StatusCode;

        Assert.Equal(others, missing);
    }

    [Fact]
    public async Task AnSdkKeyOnlyWorksForItsOwnEnvironment()
    {
        Workspace a = await Workspace.CreateAsync(factory);
        Workspace b = await Workspace.CreateAsync(factory);
        IssuedKey keyA = await a.CreateKeyAsync();
        IssuedKey keyB = await b.CreateKeyAsync();
        (await a.Client.PostAsJsonAsync($"/v1/projects/{a.Project}/environments", new { key = "second", name = "Second" }))
            .EnsureSuccessStatusCode();

        HttpClient sdkA = factory.ClientWithKey(keyA.Secret);

        Assert.Equal(HttpStatusCode.OK, (await sdkA.GetAsync($"/v1/config/{a.Environment}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await sdkA.GetAsync($"/v1/config/{b.Environment}")).StatusCode); // other project
        Assert.Equal(HttpStatusCode.NotFound, (await sdkA.GetAsync("/v1/config/second")).StatusCode); // same project, other environment
        Assert.Equal(HttpStatusCode.OK, (await factory.ClientWithKey(keyB.Secret).GetAsync($"/v1/config/{b.Environment}")).StatusCode);
    }

    // ---- roles ----

    [Fact]
    public async Task RolesGateWhatAMemberCanDo()
    {
        Workspace ws = await Workspace.CreateAsync(factory);
        HttpClient viewer = await ws.AddMemberAsync(Workspace.Unique("viewer"), "Viewer");
        HttpClient editor = await ws.AddMemberAsync(Workspace.Unique("editor"), "Editor");
        await ws.CreateExperimentAsync();
        string newExperiment = $"{ws.EnvironmentUrl}/experiments";
        object body = new { key = "second", name = "x", variations = new[] { new { key = "a", trafficBasisPoints = 1 } } };

        // Viewer: read only.
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync(newExperiment)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync(newExperiment, body)).StatusCode);

        // Editor: can change experiments, cannot administer.
        Assert.Equal(HttpStatusCode.Created, (await editor.PostAsJsonAsync(newExperiment, body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.PostAsJsonAsync($"/v1/projects/{ws.Project}/environments", new { key = "x", name = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.PostAsJsonAsync($"{ws.EnvironmentUrl}/api-keys", new { name = "k", permissions = ConfigRead })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.GetAsync($"/v1/projects/{ws.Project}/members")).StatusCode);
    }

    [Fact]
    public async Task AProjectCannotLoseItsLastAdmin()
    {
        Workspace ws = await Workspace.CreateAsync(factory);

        HttpResponseMessage demote = await ws.Client.PutAsJsonAsync($"/v1/projects/{ws.Project}/members/{ws.User}", new { role = "Viewer" });
        HttpResponseMessage remove = await ws.Client.DeleteAsync($"/v1/projects/{ws.Project}/members/{ws.User}");

        Assert.Equal(HttpStatusCode.Conflict, demote.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, remove.StatusCode);
    }

    [Fact]
    public async Task RemovedMembersLoseAccess()
    {
        Workspace ws = await Workspace.CreateAsync(factory);
        string user = Workspace.Unique("temp");
        HttpClient member = await ws.AddMemberAsync(user, "Viewer");
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync($"/v1/projects/{ws.Project}")).StatusCode);

        await ws.Client.DeleteAsync($"/v1/projects/{ws.Project}/members/{user}");

        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync($"/v1/projects/{ws.Project}")).StatusCode);
    }

    // ---- SDK keys ----

    [Fact]
    public async Task SecretIsShownOnceAndNeverListedOrStoredInTheClear()
    {
        Workspace ws = await Workspace.CreateAsync(factory);

        IssuedKey issued = await ws.CreateKeyAsync();
        string listed = await ws.Client.GetStringAsync($"{ws.EnvironmentUrl}/api-keys");

        Assert.StartsWith("sl_", issued.Secret, StringComparison.Ordinal);
        Assert.Contains(issued.Prefix, listed, StringComparison.Ordinal);
        Assert.DoesNotContain(issued.Secret, listed, StringComparison.Ordinal);

        using IServiceScope scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SplitlabDbContext>();
        ApiKey stored = await db.ApiKeys.IgnoreQueryFilters().SingleAsync(k => k.Id == issued.Id);
        Assert.NotEqual(issued.Secret, stored.KeyHash);
        Assert.DoesNotContain(issued.Secret, stored.KeyHash, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RevokedKeysStopWorking()
    {
        Workspace ws = await Workspace.CreateAsync(factory);
        IssuedKey key = await ws.CreateKeyAsync();
        HttpClient sdk = factory.ClientWithKey(key.Secret);
        Assert.Equal(HttpStatusCode.OK, (await sdk.GetAsync($"/v1/config/{ws.Environment}")).StatusCode);

        await ws.Client.PostAsync($"{ws.EnvironmentUrl}/api-keys/{key.Id}/revoke", null);

        Assert.Equal(HttpStatusCode.Unauthorized, (await sdk.GetAsync($"/v1/config/{ws.Environment}")).StatusCode);
    }

    [Fact]
    public async Task RotationIssuesANewKeyAndRetiresTheOldOne()
    {
        Workspace ws = await Workspace.CreateAsync(factory);
        IssuedKey old = await ws.CreateKeyAsync();

        HttpResponseMessage rotated = await ws.Client.PostAsync($"{ws.EnvironmentUrl}/api-keys/{old.Id}/rotate", null);
        var fresh = (await rotated.Content.ReadFromJsonAsync<IssuedKey>())!;

        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        Assert.NotEqual(old.Secret, fresh.Secret);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.ClientWithKey(old.Secret).GetAsync($"/v1/config/{ws.Environment}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await factory.ClientWithKey(fresh.Secret).GetAsync($"/v1/config/{ws.Environment}")).StatusCode);
    }

    [Fact]
    public async Task KeysWithoutConfigReadCannotFetchTheDatafile()
    {
        Workspace ws = await Workspace.CreateAsync(factory);
        IssuedKey writeOnly = await ws.CreateKeyAsync("events:write");

        HttpResponseMessage response = await factory.ClientWithKey(writeOnly.Secret).GetAsync($"/v1/config/{ws.Environment}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ATamperedKeyIsRejected()
    {
        Workspace ws = await Workspace.CreateAsync(factory);
        IssuedKey key = await ws.CreateKeyAsync();
        string tampered = key.Secret[..^1] + (key.Secret[^1] == 'A' ? 'B' : 'A');

        HttpResponseMessage response = await factory.ClientWithKey(tampered).GetAsync($"/v1/config/{ws.Environment}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task SdkKeysAndUserTokensDoNotCrossOver()
    {
        Workspace ws = await Workspace.CreateAsync(factory);
        IssuedKey key = await ws.CreateKeyAsync();

        // A key cannot manage anything; a person's token is not accepted for the datafile.
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.ClientWithKey(key.Secret).GetAsync("/v1/projects")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ws.Client.GetAsync($"/v1/config/{ws.Environment}")).StatusCode);
    }

    [Theory]
    [InlineData("nonsense")]
    [InlineData("sl_short")]
    [InlineData("sl_000000000000_nope")]
    public async Task MalformedOrUnknownKeysAreRejected(string secret)
    {
        HttpResponseMessage response = await factory.ClientWithKey(secret).GetAsync("/v1/config/anything");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- the data layer on its own (defence in depth) ----

    [Fact]
    public async Task NoTenantMeansNoRowsAndATenantSeesOnlyItsOwn()
    {
        Workspace a = await Workspace.CreateAsync(factory);
        Workspace b = await Workspace.CreateAsync(factory);
        await a.CreateExperimentAsync();
        await b.CreateExperimentAsync();

        (Guid projectA, Guid projectB) = await ProjectIdsAsync(a.Project, b.Project);

        using (IServiceScope none = factory.Services.CreateScope())
        {
            var db = none.ServiceProvider.GetRequiredService<SplitlabDbContext>();
            Assert.Empty(await db.Projects.ToListAsync());
            Assert.Empty(await db.Experiments.ToListAsync());
            Assert.Empty(await db.ApiKeys.ToListAsync());
            Assert.Empty(await db.AuditEntries.ToListAsync());
        }

        using (IServiceScope scoped = factory.Services.CreateScope())
        {
            scoped.ServiceProvider.GetRequiredService<ITenantContext>().Use(projectA);
            var db = scoped.ServiceProvider.GetRequiredService<SplitlabDbContext>();

            Assert.All(await db.Experiments.ToListAsync(), e => Assert.Equal(projectA, e.ProjectId));
            Assert.All(await db.Environments.ToListAsync(), e => Assert.Equal(projectA, e.ProjectId));
            Assert.All(await db.AuditEntries.ToListAsync(), e => Assert.Equal(projectA, e.ProjectId));
            Assert.DoesNotContain(await db.Projects.ToListAsync(), p => p.Id == projectB);
            Assert.Single(await db.Projects.ToListAsync());
        }
    }

    [Fact]
    public async Task WritingIntoAnotherTenantIsRefused()
    {
        Workspace a = await Workspace.CreateAsync(factory);
        Workspace b = await Workspace.CreateAsync(factory);
        (Guid projectA, Guid projectB) = await ProjectIdsAsync(a.Project, b.Project);

        using IServiceScope scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Use(projectA);
        var db = scope.ServiceProvider.GetRequiredService<SplitlabDbContext>();
        db.Experiments.Add(Experiment.Create(
            projectB, Guid.NewGuid(), "smuggled", "x", [new VariationSpec("a", 1)], DateTimeOffset.UtcNow));

        await Assert.ThrowsAsync<TenantIsolationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public void ARequestCannotSwitchTenants()
    {
        using IServiceScope scope = factory.Services.CreateScope();
        var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        tenant.Use(Guid.NewGuid());

        Assert.Throws<TenantIsolationException>(() => tenant.Use(Guid.NewGuid()));
    }

    private async Task<(Guid, Guid)> ProjectIdsAsync(string keyA, string keyB)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SplitlabDbContext>();
        Guid a = await db.Projects.IgnoreQueryFilters().Where(p => p.Key == keyA).Select(p => p.Id).SingleAsync();
        Guid b = await db.Projects.IgnoreQueryFilters().Where(p => p.Key == keyB).Select(p => p.Id).SingleAsync();
        return (a, b);
    }
}
