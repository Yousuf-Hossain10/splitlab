using Microsoft.EntityFrameworkCore;
using Splitlab.Domain.Audit;
using Splitlab.Domain.Experiments;
using Splitlab.Domain.Projects;

namespace Splitlab.Infrastructure.Persistence;

public sealed class SplitlabDbContext(DbContextOptions<SplitlabDbContext> options) : DbContext(options)
{
    public DbSet<Project> Projects => Set<Project>();

    public DbSet<ProjectEnvironment> Environments => Set<ProjectEnvironment>();

    public DbSet<Experiment> Experiments => Set<Experiment>();

    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<Project>(b =>
        {
            b.ToTable("projects");
            b.HasKey(p => p.Id);
            b.Property(p => p.Key).HasMaxLength(64).IsRequired();
            b.Property(p => p.Name).HasMaxLength(200).IsRequired();
            b.HasIndex(p => p.Key).IsUnique();
        });

        modelBuilder.Entity<ProjectEnvironment>(b =>
        {
            b.ToTable("environments");
            b.HasKey(e => e.Id);
            b.Property(e => e.Key).HasMaxLength(64).IsRequired();
            b.Property(e => e.Name).HasMaxLength(200).IsRequired();
            b.Property(e => e.ConfigVersion).IsConcurrencyToken();
            b.HasIndex(e => e.Key).IsUnique();
            b.HasIndex(e => e.ProjectId);
            b.HasOne<Project>().WithMany().HasForeignKey(e => e.ProjectId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Experiment>(b =>
        {
            b.ToTable("experiments");
            b.HasKey(e => e.Id);
            b.Property(e => e.Key).HasMaxLength(64).IsRequired();
            b.Property(e => e.Name).HasMaxLength(200).IsRequired();
            b.Property(e => e.Status).HasConversion<string>().HasMaxLength(16);
            b.Property(e => e.Version).IsConcurrencyToken();
            b.HasIndex(e => new { e.EnvironmentId, e.Key }).IsUnique();
            b.HasIndex(e => e.ProjectId);
            b.HasOne<ProjectEnvironment>().WithMany().HasForeignKey(e => e.EnvironmentId).OnDelete(DeleteBehavior.Restrict);

            b.Ignore(e => e.Variations);
            b.OwnsMany<Variation>("_variations", v =>
            {
                v.ToTable("experiment_variations");
                v.WithOwner().HasForeignKey("ExperimentId");
                v.HasKey(x => x.Id);
                v.Property(x => x.Key).HasMaxLength(64).IsRequired();
            });
        });

        modelBuilder.Entity<AuditEntry>(b =>
        {
            b.ToTable("audit_log");
            b.HasKey(a => a.Id);
            b.Property(a => a.Actor).HasMaxLength(200).IsRequired();
            b.Property(a => a.Action).HasMaxLength(100).IsRequired();
            b.Property(a => a.EntityType).HasMaxLength(100).IsRequired();
            b.Property(a => a.Detail).HasMaxLength(1000);
            b.HasIndex(a => new { a.ProjectId, a.At });
        });
    }
}
