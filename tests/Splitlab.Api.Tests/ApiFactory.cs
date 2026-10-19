using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Splitlab.Api.Auth;
using Splitlab.Infrastructure.Persistence;

namespace Splitlab.Api.Tests;

/// <summary>Hosts the real API over an in-memory SQLite database that lives as long as the factory.</summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public const string SigningKey = "test-signing-key-test-signing-key-0123456789";

    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        _connection.Open();

        builder.UseEnvironment("Testing");
        builder.UseSetting("Auth:Local:SigningKey", SigningKey);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<SplitlabDbContext>>();
            services.AddDbContext<SplitlabDbContext>(o => o.UseSqlite(_connection));
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        IHost host = base.CreateHost(builder);
        using IServiceScope scope = host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SplitlabDbContext>().Database.EnsureCreated();
        return host;
    }

    /// <summary>A client signed in as <paramref name="subject"/> through the local development issuer.</summary>
    public HttpClient ClientFor(string subject)
    {
        HttpClient client = CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", AuthSetup.MintLocalToken(SigningKey, subject, TimeSpan.FromMinutes(5)));
        return client;
    }

    /// <summary>A client presenting an SDK key.</summary>
    public HttpClient ClientWithKey(string secret)
    {
        HttpClient client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _connection.Dispose();
        }
    }
}

/// <summary>A signed-in user who owns a fresh project with one environment.</summary>
public sealed class Workspace
{
    private Workspace(ApiFactory factory, string user, HttpClient client, string project, string environment)
    {
        Factory = factory;
        User = user;
        Client = client;
        Project = project;
        Environment = environment;
    }

    public ApiFactory Factory { get; }

    public string User { get; }

    public HttpClient Client { get; }

    public string Project { get; }

    public string Environment { get; }

    public string EnvironmentUrl => $"/v1/projects/{Project}/environments/{Environment}";

    public static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..20];

    public static async Task<Workspace> CreateAsync(ApiFactory factory)
    {
        string user = Unique("user");
        HttpClient client = factory.ClientFor(user);
        string project = Unique("proj");
        string environment = Unique("env");

        (await client.PostAsJsonAsync("/v1/projects", new { key = project, name = "P" })).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync($"/v1/projects/{project}/environments", new { key = environment, name = "E" }))
            .EnsureSuccessStatusCode();
        return new Workspace(factory, user, client, project, environment);
    }

    /// <summary>Creates a draft two-way experiment.</summary>
    public async Task<ExperimentView> CreateExperimentAsync(string key = "checkout")
    {
        HttpResponseMessage response = await Client.PostAsJsonAsync(
            $"{EnvironmentUrl}/experiments",
            new
            {
                key,
                name = "Checkout",
                variations = new[]
                {
                    new { key = "control", trafficBasisPoints = 5_000 },
                    new { key = "treatment", trafficBasisPoints = 5_000 },
                },
            });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ExperimentView>())!;
    }

    public async Task<IssuedKey> CreateKeyAsync(params string[] permissions)
    {
        HttpResponseMessage response = await Client.PostAsJsonAsync(
            $"{EnvironmentUrl}/api-keys",
            new { name = "server", permissions = permissions.Length == 0 ? ["config:read"] : permissions });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<IssuedKey>())!;
    }

    /// <summary>Adds <paramref name="user"/> to the project with a role and returns a client signed in as them.</summary>
    public async Task<HttpClient> AddMemberAsync(string user, string role)
    {
        (await Client.PutAsJsonAsync($"/v1/projects/{Project}/members/{user}", new { role })).EnsureSuccessStatusCode();
        return Factory.ClientFor(user);
    }
}

public sealed record ExperimentView(string Key, string Status, int Version);

public sealed record IssuedKey(Guid Id, string Name, string Prefix, string[] Permissions, string Secret);
