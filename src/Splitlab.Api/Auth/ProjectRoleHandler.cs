using Microsoft.AspNetCore.Authorization;
using Splitlab.Application;
using Splitlab.Domain.Projects;

namespace Splitlab.Api.Auth;

public sealed class ProjectRoleRequirement(ProjectRole minimum) : IAuthorizationRequirement
{
    public ProjectRole Minimum { get; } = minimum;
}

/// <summary>
/// Grants access when the caller holds at least the required role in the project named by the <c>{project}</c> route value,
/// and scopes the request to that project. An unknown project and a project the caller is not in look identical (403),
/// so project keys cannot be probed.
/// </summary>
internal sealed class ProjectRoleHandler(IAccessRepository access, ITenantContext tenant, ICurrentUser user)
    : AuthorizationHandler<ProjectRoleRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, ProjectRoleRequirement requirement)
    {
        if (context.Resource is not HttpContext http
            || user.Subject is not { } subject
            || http.GetRouteValue("project") is not string projectKey)
        {
            return;
        }

        ProjectAccess? granted = await access.GetAccessAsync(projectKey, subject, http.RequestAborted);
        if (granted is null || granted.Role < requirement.Minimum)
        {
            return;
        }

        tenant.Use(granted.ProjectId);
        context.Succeed(requirement);
    }
}
