using Microsoft.EntityFrameworkCore;
using Splitlab.Api;
using Splitlab.Infrastructure.Persistence;

string connectionString = "";

WebApplication app = SplitlabApp.Create(
    args,
    options => options.UseNpgsql(connectionString),
    // Runs before the app is built, so a missing connection string stops startup rather than the first request.
    builder => connectionString = builder.Configuration.GetConnectionString("Splitlab")
        ?? throw new InvalidOperationException("Connection string 'Splitlab' is not configured."));

if (app.Environment.IsDevelopment())
{
    // Convenience for local runs; production applies migrations as a deployment step.
    using IServiceScope scope = app.Services.CreateScope();
    scope.ServiceProvider.GetRequiredService<SplitlabDbContext>().Database.Migrate();
}

app.Run();

/// <summary>Entry point marker so integration tests can host the app.</summary>
public partial class Program;
