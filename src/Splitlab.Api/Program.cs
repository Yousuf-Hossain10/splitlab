using System.Threading.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Splitlab.Api;
using Splitlab.Api.Auth;
using Splitlab.Application;
using Splitlab.Domain.Statistics;
using Splitlab.Infrastructure;
using Splitlab.Infrastructure.Events;
using Splitlab.Infrastructure.Persistence;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

string connectionString = builder.Configuration.GetConnectionString("Splitlab")
    ?? throw new InvalidOperationException("Connection string 'Splitlab' is not configured.");

builder.Services.AddApplication();
builder.Services.AddInfrastructure(o => o.UseNpgsql(connectionString));
builder.Services.AddSingleton(builder.Configuration.GetSection("Stats").Get<AnalysisOptions>() ?? new AnalysisOptions());
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
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
            context.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
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

if (app.Environment.IsDevelopment())
{
    // Convenience for local runs; production applies migrations as a deployment step.
    using IServiceScope scope = app.Services.CreateScope();
    scope.ServiceProvider.GetRequiredService<SplitlabDbContext>().Database.Migrate();
}

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

app.Run();

/// <summary>Entry point marker so integration tests can host the app.</summary>
public partial class Program;
