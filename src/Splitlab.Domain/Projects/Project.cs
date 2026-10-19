namespace Splitlab.Domain.Projects;

/// <summary>The tenant boundary: a team's projects, environments and experiments are invisible to other projects (ADR-0010).</summary>
public sealed class Project
{
    private Project()
    {
    }

    public Guid Id { get; private set; }

    public string Key { get; private set; } = "";

    public string Name { get; private set; } = "";

    public DateTimeOffset CreatedAt { get; private set; }

    public static Project Create(string? key, string? name, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        Key = Keys.Validate(key, "Project key"),
        Name = Keys.RequireName(name, "Project name"),
        CreatedAt = now,
    };
}

/// <summary>A deployment target of a project, such as production. Owns the version of its published datafile.</summary>
public sealed class ProjectEnvironment : ITenantOwned
{
    private ProjectEnvironment()
    {
    }

    public Guid Id { get; private set; }

    public Guid ProjectId { get; private set; }

    /// <summary>Unique across all projects for now, because the SDK addresses its datafile by this key.</summary>
    public string Key { get; private set; } = "";

    public string Name { get; private set; } = "";

    /// <summary>Incremented whenever the set of running experiments changes; becomes the datafile ETag.</summary>
    public int ConfigVersion { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static ProjectEnvironment Create(Guid projectId, string? key, string? name, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = projectId,
        Key = Keys.Validate(key, "Environment key"),
        Name = Keys.RequireName(name, "Environment name"),
        CreatedAt = now,
    };

    public void BumpConfigVersion() => ConfigVersion++;
}
