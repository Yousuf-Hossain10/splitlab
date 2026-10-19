using Microsoft.EntityFrameworkCore;
using Splitlab.Api;
using Splitlab.Api.Auth;
using Splitlab.Application;
using Splitlab.Infrastructure;
using Splitlab.Infrastructure.Persistence;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

string connectionString = builder.Configuration.GetConnectionString("Splitlab")
    ?? throw new InvalidOperationException("Connection string 'Splitlab' is not configured.");

builder.Services.AddApplication();
builder.Services.AddInfrastructure(o => o.UseNpgsql(connectionString));
builder.Services.AddSplitlabAuth(builder.Configuration);
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
app.UseAuthentication();
app.UseAuthorization();

app.MapManagementEndpoints();
app.MapConfigEndpoints();

string? localKey = app.Configuration["Auth:Local:SigningKey"];
if (app.Environment.IsDevelopment() && string.IsNullOrWhiteSpace(app.Configuration["Auth:Authority"]) && localKey is not null)
{
    app.MapDevTokenEndpoint(localKey);
}

app.Run();

/// <summary>Entry point marker so integration tests can host the app.</summary>
public partial class Program;
