using Microsoft.Extensions.DependencyInjection;

namespace Splitlab.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<ProjectService>();
        services.AddScoped<ExperimentService>();
        services.AddScoped<DatafileService>();
        services.AddScoped<MemberService>();
        services.AddScoped<ApiKeyService>();
        return services;
    }
}
