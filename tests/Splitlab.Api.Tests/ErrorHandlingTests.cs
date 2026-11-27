using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Splitlab.Api.Tests;

/// <summary>
/// An expected refusal (not found, conflict, invalid input) is a normal answer, not a fault, so it must not look like one in the
/// logs; a genuine bug must still be logged as an error. Mixing the two makes real errors impossible to spot or alert on.
/// </summary>
public sealed class ErrorHandlingTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    /// <summary>Adds a route that fails with an unexpected exception, reached only if nothing else matched (so inside the error handling).</summary>
    private sealed class ThrowingRoute : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            next(app);
            app.Map("/__boom", branch => branch.Run(_ => throw new InvalidOperationException("kaboom: a genuine bug")));
        };
    }

    private IEnumerable<LogSink.LogEntry> Errors => factory.Logs.Entries.Where(e => e.Level >= LogLevel.Error);

    [Fact]
    public async Task ExpectedRefusalsAreAnsweredCleanlyAndNeverLoggedAsErrors()
    {
        Workspace ws = await Workspace.CreateAsync(factory);
        await ws.CreateExperimentAsync();
        factory.Logs.Clear();

        HttpResponseMessage conflict = await factory.ClientFor("someone-else").PostAsJsonAsync("/v1/projects", new { key = ws.Project, name = "P" });
        HttpResponseMessage missing = await ws.Client.GetAsync($"{ws.EnvironmentUrl}/experiments/nope");
        HttpResponseMessage started = await ws.Client.PostAsJsonAsync($"{ws.EnvironmentUrl}/experiments/checkout/start", new { version = 1 });
        HttpResponseMessage stale = await ws.Client.PostAsJsonAsync($"{ws.EnvironmentUrl}/experiments/checkout/start", new { version = 99 });
        HttpResponseMessage badInput = await factory.ClientFor("x").PostAsJsonAsync("/v1/projects", new { key = "Bad Key", name = "P" });

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badInput.StatusCode);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode); // a normal success, so the log is not empty for the wrong reason
        Assert.All([conflict, missing, stale, badInput], r => Assert.Equal("application/problem+json", r.Content.Headers.ContentType?.MediaType));
        Assert.Empty(Errors);
        Assert.DoesNotContain(factory.Logs.Entries, e => e.Message.Contains("unhandled exception", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ARefusalStillTellsTheCallerWhatWentWrong()
    {
        Workspace ws = await Workspace.CreateAsync(factory);

        HttpResponseMessage response = await ws.Client.GetAsync($"{ws.EnvironmentUrl}/experiments/nope");
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(404, body.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("Not found", body.RootElement.GetProperty("title").GetString());
        Assert.Contains("nope", body.RootElement.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARefusalIsStillRecordedAtALowerLevelSoItCanBeInvestigated()
    {
        Workspace ws = await Workspace.CreateAsync(factory);
        factory.Logs.Clear();

        await ws.Client.GetAsync($"{ws.EnvironmentUrl}/experiments/nope");

        LogSink.LogEntry entry = Assert.Single(factory.Logs.Entries, e => e.Message.Contains("404", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Null(entry.Exception); // no stack trace for something that is not a bug
    }

    [Fact]
    public async Task AGenuineBugIsStillLoggedAsAnErrorWithItsExceptionAndAnswers500WithoutLeakingDetails()
    {
        await using WebApplicationFactoryScope scope = new(factory);
        factory.Logs.Clear();

        HttpResponseMessage response = await scope.Client.GetAsync("/__boom");
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("kaboom", body, StringComparison.Ordinal); // internals stay out of the response
        LogSink.LogEntry error = Assert.Single(Errors);
        Assert.IsType<InvalidOperationException>(error.Exception);
        Assert.Contains("kaboom", error.Exception!.Message, StringComparison.Ordinal);
    }

    private sealed class WebApplicationFactoryScope : IAsyncDisposable
    {
        private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _inner;

        public WebApplicationFactoryScope(ApiFactory parent)
        {
            _inner = parent.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddTransient<IStartupFilter, ThrowingRoute>()));
            Client = _inner.CreateClient();
        }

        public HttpClient Client { get; }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _inner.DisposeAsync();
        }
    }
}
