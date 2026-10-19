using System.Globalization;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Splitlab.Api.Auth;
using Splitlab.Application;
using Splitlab.Domain.Statistics;
using Splitlab.Infrastructure;
using Splitlab.Infrastructure.Events;

namespace Splitlab.Api;

/// <summary>
/// Builds the whole application. The real entry point uses PostgreSQL; the end-to-end test host builds exactly the
/// same application over SQLite, so the only thing that differs between them is the database provider.
/// </summary>
public static class SplitlabApp
{
    /// <param name="args">Command-line arguments, as for <c>WebApplication.CreateBuilder</c>.</param>
    /// <param name="configureDatabase">Selects the database provider, e.g. <c>o =&gt; o.UseNpgsql(connectionString)</c>.</param>
    /// <param name="configure">Optional changes to the builder (such as extra configuration) before the app is built.</param>
    public static WebApplication Create(
        string[] args, Action<DbContextOptionsBuilder> configureDatabase, Action<WebApplicationBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(configureDatabase);

        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
        configure?.Invoke(builder);

        builder.Services.AddApplication();
        builder.Services.AddInfrastructure(configureDatabase);
        builder.Services.AddSingleton(builder.Configuration.GetSection("Stats").Get<AnalysisOptions>() ?? new AnalysisOptions());
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        builder.Services.Configure<EventPipelineOptions>(builder.Configuration.GetSection("EventPipeline"));
        builder.Services.AddSplitlabAuth(builder.Configuration);

        // Event batches are at most 500 small events; refuse anything much larger before reading it.
        builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 1_048_576);

        // Per SDK key (ADR-0010). Runs after authorization, so only verified keys get a partition of their own.
        int eventsPerMinute = builder.Configuration.GetValue("RateLimits:EventsPerMinute", 600);
        builder.Services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.OnRejected = (context, _) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                }

                return ValueTask.CompletedTask;
            };
            o.AddPolicy(RateLimitPolicies.SdkEvents, context => RateLimitPartition.GetFixedWindowLimiter(
                context.User.FindFirst(SdkClaims.KeyPrefix)?.Value ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = eventsPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
        builder.Services.AddSplitlabOpenApi();
        builder.Services.AddProblemDetails();
        builder.Services.AddExceptionHandler<ApiExceptionHandler>();

        WebApplication app = builder.Build();

        app.UseExceptionHandler();
        app.UseSwagger();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();

        app.MapManagementEndpoints();
        app.MapConfigEndpoints();
        app.MapEventEndpoints();

        string? localKey = app.Configuration["Auth:Local:SigningKey"];
        if (app.Environment.IsDevelopment() && string.IsNullOrWhiteSpace(app.Configuration["Auth:Authority"]) && localKey is not null)
        {
            app.MapDevTokenEndpoint(localKey);
        }

        return app;
    }
}
