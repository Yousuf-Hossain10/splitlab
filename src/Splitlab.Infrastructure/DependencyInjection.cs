using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Splitlab.Application;
using Splitlab.Infrastructure.Persistence;

namespace Splitlab.Infrastructure;

public static class DependencyInjection
{
    /// <param name="configureDatabase">Selects the provider, e.g. <c>o =&gt; o.UseNpgsql(connectionString)</c>.</param>
    /// <remarks>The host must also register an <see cref="ICurrentUser"/>.</remarks>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services, Action<DbContextOptionsBuilder> configureDatabase)
    {
        services.AddDbContext<SplitlabDbContext>(configureDatabase);
        services.AddScoped<ITenantContext, TenantContext>();
        services.AddScoped<IAccessRepository, AccessRepository>();
        services.AddScoped<IMemberRepository, MemberRepository>();
        services.AddScoped<IApiKeyRepository, ApiKeyRepository>();
        services.AddScoped<IProjectRepository, ProjectRepository>();
        services.AddScoped<IExperimentRepository, ExperimentRepository>();
        services.AddScoped<IAuditLog, AuditLog>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        return services;
    }
}
