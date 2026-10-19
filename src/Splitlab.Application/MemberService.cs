using Splitlab.Domain.Projects;

namespace Splitlab.Application;

public sealed class MemberService(
    IProjectRepository projects,
    IMemberRepository members,
    IAuditLog audit,
    IUnitOfWork unitOfWork,
    TimeProvider clock)
{
    public Task<IReadOnlyList<ProjectMember>> ListAsync(CancellationToken ct) => members.ListAsync(ct);

    /// <summary>Adds a member or changes an existing member's role.</summary>
    public async Task<ProjectMember> SetRoleAsync(string projectKey, string subject, ProjectRole role, CancellationToken ct)
    {
        Project project = await GetProjectAsync(projectKey, ct);
        ProjectMember? member = await members.FindAsync(subject, ct);

        if (member is null)
        {
            member = ProjectMember.Create(project.Id, subject, role, clock.GetUtcNow());
            members.Add(member);
        }
        else
        {
            if (member.Role == ProjectRole.Admin && role != ProjectRole.Admin)
            {
                await EnsureAnotherAdminAsync(ct);
            }

            member.ChangeRole(role);
        }

        audit.Record(project.Id, "member.role_set", nameof(ProjectMember), member.Id, $"{subject}={role}");
        await unitOfWork.CommitAsync(ct);
        return member;
    }

    public async Task RemoveAsync(string projectKey, string subject, CancellationToken ct)
    {
        Project project = await GetProjectAsync(projectKey, ct);
        ProjectMember member = await members.FindAsync(subject, ct)
            ?? throw new NotFoundException($"'{subject}' is not a member of this project.");

        if (member.Role == ProjectRole.Admin)
        {
            await EnsureAnotherAdminAsync(ct);
        }

        members.Remove(member);
        audit.Record(project.Id, "member.removed", nameof(ProjectMember), member.Id, subject);
        await unitOfWork.CommitAsync(ct);
    }

    private async Task<Project> GetProjectAsync(string key, CancellationToken ct) =>
        await projects.FindProjectAsync(key, ct) ?? throw new NotFoundException($"Project '{key}' was not found.");

    private async Task EnsureAnotherAdminAsync(CancellationToken ct)
    {
        if (await members.CountAdminsAsync(ct) <= 1)
        {
            throw new ConflictException("A project must keep at least one Admin.");
        }
    }
}
