using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Splitlab.Infrastructure.Persistence;

/// <summary>Lets <c>dotnet ef</c> build the context without starting the API. Never connects; used only to generate migrations.</summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<SplitlabDbContext>
{
    public SplitlabDbContext CreateDbContext(string[] args) =>
        new(
            new DbContextOptionsBuilder<SplitlabDbContext>()
                .UseNpgsql("Host=localhost;Database=splitlab_design_time")
                .Options,
            new TenantContext());
}
