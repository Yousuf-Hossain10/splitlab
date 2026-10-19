using Microsoft.EntityFrameworkCore;
using Splitlab.Application;
using Splitlab.Domain.Audit;
using Splitlab.Domain.Events;
using Splitlab.Domain.Experiments;
using Splitlab.Domain.Projects;
using Splitlab.Domain.Statistics;

namespace Splitlab.Infrastructure.Persistence;

/// <summary>A write was attempted for a project other than the one the request is scoped to. Always a bug, never user error.</summary>
public sealed class TenantIsolationException(string message) : InvalidOperationException(message);

public sealed class SplitlabDbContext(DbContextOptions<SplitlabDbContext> options, ITenantContext tenant) : DbContext(options)
{
    public DbSet<Project> Projects => Set<Project>();

    public DbSet<ProjectEnvironment> Environments => Set<ProjectEnvironment>();

    public DbSet<Experiment> Experiments => Set<Experiment>();

    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    public DbSet<ProjectMember> Members => Set<ProjectMember>();

    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

    public DbSet<EventRecord> Events => Set<EventRecord>();

    public DbSet<ExperimentUserState> ExperimentUserStates => Set<ExperimentUserState>();

    public DbSet<MetricUserState> MetricUserStates => Set<MetricUserState>();

    // Read by the global query filters on every query. With no tenant set this is Guid.Empty,
    // which matches no row, so forgetting to scope a request fails closed (ADR-0010).
    private Guid CurrentProjectId => tenant.ProjectId ?? Guid.Empty;

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
            b.HasQueryFilter(p => p.Id == CurrentProjectId);
        });

        modelBuilder.Entity<ProjectEnvironment>(b =>
        {
            b.ToTable("environments");
            b.HasKey(e => e.Id);
            b.Property(e => e.Key).HasMaxLength(64).IsRequired();
            b.Property(e => e.Name).HasMaxLength(200).IsRequired();
            b.Property(e => e.ConfigVersion).IsConcurrencyToken();
            b.HasIndex(e => new { e.ProjectId, e.Key }).IsUnique();
            b.HasOne<Project>().WithMany().HasForeignKey(e => e.ProjectId).OnDelete(DeleteBehavior.Restrict);
            b.HasQueryFilter(e => e.ProjectId == CurrentProjectId);
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
            b.HasQueryFilter(e => e.ProjectId == CurrentProjectId);

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
            b.HasQueryFilter(a => a.ProjectId == CurrentProjectId);
        });

        modelBuilder.Entity<ProjectMember>(b =>
        {
            b.ToTable("project_members");
            b.HasKey(m => m.Id);
            b.Property(m => m.Subject).HasMaxLength(200).IsRequired();
            b.Property(m => m.Role).HasConversion<string>().HasMaxLength(16);
            b.HasIndex(m => new { m.ProjectId, m.Subject }).IsUnique();
            b.HasIndex(m => m.Subject);
            b.HasQueryFilter(m => m.ProjectId == CurrentProjectId);
        });

        // High-volume and append-only, so no foreign keys. The key includes OccurredAt so the table can be
        // partitioned by time later (a PostgreSQL unique key must contain the partition column). Written by
        // EventStore with raw SQL; this mapping serves reads such as rollups.
        modelBuilder.Entity<EventRecord>(b =>
        {
            b.ToTable("events");
            b.HasKey(e => new { e.EnvironmentId, e.EventId, e.OccurredAt });
            b.Property(e => e.EventId).HasMaxLength(EventRecord.MaxEventIdLength);
            b.Property(e => e.UserId).HasMaxLength(EventRecord.MaxUserIdLength);
            b.Property(e => e.Kind).HasConversion<string>().HasMaxLength(16);
            b.Property(e => e.ExperimentKey).HasMaxLength(64);
            b.Property(e => e.VariationKey).HasMaxLength(64);
            b.Property(e => e.Name).HasMaxLength(64);
            b.HasIndex(e => new { e.EnvironmentId, e.OccurredAt });
            b.HasIndex(e => new { e.EnvironmentId, e.ExperimentKey, e.Kind });
            b.HasIndex(e => e.ProjectId);
            b.HasQueryFilter(e => e.ProjectId == CurrentProjectId);
        });

        // Rollups behind results (ADR-0004): one small row per user per experiment, and per user per metric. Written with
        // upserts by RollupWriter inside the transaction that stores the events; this mapping serves reads.
        modelBuilder.Entity<ExperimentUserState>(b =>
        {
            b.ToTable("experiment_user_states");
            b.HasKey(s => new { s.EnvironmentId, s.ExperimentKey, s.UserId });
            b.Property(s => s.ExperimentKey).HasMaxLength(64);
            b.Property(s => s.UserId).HasMaxLength(EventRecord.MaxUserIdLength);
            b.Property(s => s.VariationKey).HasMaxLength(64);
            b.HasQueryFilter(s => s.ProjectId == CurrentProjectId);
        });

        modelBuilder.Entity<MetricUserState>(b =>
        {
            b.ToTable("metric_user_states");
            b.HasKey(s => new { s.EnvironmentId, s.Metric, s.UserId });
            b.Property(s => s.Metric).HasMaxLength(64);
            b.Property(s => s.UserId).HasMaxLength(EventRecord.MaxUserIdLength);
            b.HasQueryFilter(s => s.ProjectId == CurrentProjectId);
        });

        modelBuilder.Entity<ApiKey>(b =>
        {
            b.ToTable("api_keys");
            b.HasKey(k => k.Id);
            b.Property(k => k.Name).HasMaxLength(200).IsRequired();
            b.Property(k => k.Prefix).HasMaxLength(16).IsRequired();
            b.Property(k => k.KeyHash).HasMaxLength(64).IsRequired();
            b.HasIndex(k => k.Prefix).IsUnique();
            b.HasIndex(k => k.EnvironmentId);
            b.HasQueryFilter(k => k.ProjectId == CurrentProjectId);
        });
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnforceTenantOnWrites();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        EnforceTenantOnWrites();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>Defence in depth: even if a use case builds the wrong entity, it cannot be written into another tenant.</summary>
    private void EnforceTenantOnWrites()
    {
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
            {
                continue;
            }

            Guid? owner = entry.Entity switch
            {
                Project p => p.Id,
                ITenantOwned t => t.ProjectId,
                _ => null,
            };

            if (owner is not null && owner != tenant.ProjectId)
            {
                throw new TenantIsolationException(
                    $"Refusing to write {entry.Metadata.ClrType.Name} for project {owner}: the request is scoped to {tenant.ProjectId?.ToString() ?? "no project"}.");
            }
        }
    }
}
