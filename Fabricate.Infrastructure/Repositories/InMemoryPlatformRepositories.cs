using System.Collections.Concurrent;
using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;

namespace Fabricate.Infrastructure.Repositories;

// In-memory adapters for the platform aggregates introduced in #65. Grouped in one file because they are
// uniform and exist only as the default/no-database configuration; the EF adapters are the production path.

public sealed class InMemoryWorkspaceRepository : IWorkspaceRepository
{
    private readonly ConcurrentDictionary<Guid, Workspace> _workspaces = new();
    private readonly ConcurrentDictionary<(Guid WorkspaceId, Guid PrincipalId, bool IsGroup), WorkspaceMembership> _memberships = new();

    public Task<Workspace> SaveAsync(Workspace workspace, CancellationToken cancellationToken = default)
    {
        _workspaces[workspace.Id] = workspace;
        return Task.FromResult(workspace);
    }

    public Task<Workspace?> GetByIdAsync(Guid workspaceId, CancellationToken cancellationToken = default)
        => Task.FromResult(_workspaces.GetValueOrDefault(workspaceId));

    public Task<IReadOnlyList<Workspace>> ListByAccountAsync(Guid accountId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<Workspace>>(_workspaces.Values.Where(w => w.AccountId == accountId).OrderBy(w => w.CreatedAt).ToArray());

    public Task<WorkspaceMembership> SaveMembershipAsync(WorkspaceMembership membership, CancellationToken cancellationToken = default)
    {
        _memberships[(membership.WorkspaceId, membership.PrincipalId, membership.IsGroup)] = membership;
        return Task.FromResult(membership);
    }

    public Task RemoveMembershipAsync(Guid workspaceId, Guid principalId, bool isGroup, CancellationToken cancellationToken = default)
    {
        _memberships.TryRemove((workspaceId, principalId, isGroup), out _);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<WorkspaceMembership>> ListMembershipsAsync(Guid workspaceId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<WorkspaceMembership>>(_memberships.Values.Where(m => m.WorkspaceId == workspaceId).ToArray());
}

public sealed class InMemoryConnectionRepository : IConnectionRepository
{
    private readonly ConcurrentDictionary<Guid, Connection> _connections = new();

    public Task<Connection> SaveAsync(Connection connection, CancellationToken cancellationToken = default)
    {
        _connections[connection.Id] = connection;
        return Task.FromResult(connection);
    }

    public Task<Connection?> GetByIdAsync(Guid connectionId, CancellationToken cancellationToken = default)
        => Task.FromResult(_connections.GetValueOrDefault(connectionId));

    public Task<IReadOnlyList<Connection>> ListByWorkspaceAsync(Guid workspaceId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<Connection>>(_connections.Values.Where(c => c.WorkspaceId == workspaceId).OrderBy(c => c.CreatedAt).ToArray());

    public Task DeleteAsync(Guid connectionId, CancellationToken cancellationToken = default)
    {
        _connections.TryRemove(connectionId, out _);
        return Task.CompletedTask;
    }
}

public sealed class InMemoryInstructionVersionRepository : IInstructionVersionRepository
{
    private readonly ConcurrentDictionary<Guid, InstructionVersion> _versions = new();

    public Task<InstructionVersion> SaveAsync(InstructionVersion version, CancellationToken cancellationToken = default)
    {
        _versions[version.Id] = version;
        return Task.FromResult(version);
    }

    public Task<IReadOnlyList<InstructionVersion>> ListByWorkspaceAsync(Guid workspaceId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<InstructionVersion>>(
            _versions.Values.Where(v => v.ProjectId == null && v.WorkspaceId == workspaceId).OrderByDescending(v => v.Version).ToArray());

    public Task<IReadOnlyList<InstructionVersion>> ListByProjectAsync(Guid projectId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<InstructionVersion>>(
            _versions.Values.Where(v => v.ProjectId == projectId).OrderByDescending(v => v.Version).ToArray());
}

public sealed class InMemoryProjectDatabaseRepository : IProjectDatabaseRepository
{
    private readonly ConcurrentDictionary<Guid, ProjectDatabase> _databases = new();

    public Task<ProjectDatabase> SaveAsync(ProjectDatabase database, CancellationToken cancellationToken = default)
    {
        _databases[database.Id] = database;
        return Task.FromResult(database);
    }

    public Task<ProjectDatabase?> GetByIdAsync(Guid databaseId, CancellationToken cancellationToken = default)
        => Task.FromResult(_databases.GetValueOrDefault(databaseId));

    public Task<IReadOnlyList<ProjectDatabase>> ListByProjectAsync(Guid projectId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<ProjectDatabase>>(_databases.Values.Where(d => d.ProjectId == projectId).OrderBy(d => d.CreatedAt).ToArray());

    public Task DeleteAsync(Guid databaseId, CancellationToken cancellationToken = default)
    {
        _databases.TryRemove(databaseId, out _);
        return Task.CompletedTask;
    }
}

public sealed class InMemoryWorkflowRepository : IWorkflowRepository
{
    private readonly ConcurrentDictionary<Guid, Workflow> _workflows = new();
    private readonly ConcurrentDictionary<Guid, WorkflowStep> _steps = new();
    private readonly ConcurrentDictionary<Guid, WorkflowRun> _runs = new();
    private readonly ConcurrentDictionary<Guid, WorkflowStepRun> _stepRuns = new();

    public Task<Workflow> SaveAsync(Workflow workflow, CancellationToken cancellationToken = default)
    {
        _workflows[workflow.Id] = workflow;
        return Task.FromResult(workflow);
    }

    public Task<Workflow?> GetByIdAsync(Guid workflowId, CancellationToken cancellationToken = default)
        => Task.FromResult(_workflows.GetValueOrDefault(workflowId));

    public Task<IReadOnlyList<Workflow>> ListByWorkspaceAsync(Guid workspaceId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<Workflow>>(_workflows.Values.Where(w => w.WorkspaceId == workspaceId).OrderBy(w => w.CreatedAt).ToArray());

    public Task<WorkflowStep> SaveStepAsync(WorkflowStep step, CancellationToken cancellationToken = default)
    {
        _steps[step.Id] = step;
        return Task.FromResult(step);
    }

    public Task<IReadOnlyList<WorkflowStep>> ListStepsAsync(Guid workflowId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<WorkflowStep>>(_steps.Values.Where(s => s.WorkflowId == workflowId).OrderBy(s => s.StepOrder).ToArray());

    public Task<WorkflowRun> SaveRunAsync(WorkflowRun run, CancellationToken cancellationToken = default)
    {
        _runs[run.Id] = run;
        return Task.FromResult(run);
    }

    public Task<WorkflowRun?> GetRunAsync(Guid runId, CancellationToken cancellationToken = default)
        => Task.FromResult(_runs.GetValueOrDefault(runId));

    public Task<WorkflowStepRun> SaveStepRunAsync(WorkflowStepRun stepRun, CancellationToken cancellationToken = default)
    {
        _stepRuns[stepRun.Id] = stepRun;
        return Task.FromResult(stepRun);
    }

    public Task<IReadOnlyList<WorkflowStepRun>> ListStepRunsAsync(Guid runId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<WorkflowStepRun>>(_stepRuns.Values.Where(sr => sr.WorkflowRunId == runId).OrderBy(sr => sr.StepOrder).ToArray());
}

public sealed class InMemorySkillRepository : ISkillRepository
{
    private readonly ConcurrentDictionary<Guid, Skill> _skills = new();

    public Task<Skill> SaveAsync(Skill skill, CancellationToken cancellationToken = default)
    {
        _skills[skill.Id] = skill;
        return Task.FromResult(skill);
    }

    public Task<Skill?> GetByIdAsync(Guid skillId, CancellationToken cancellationToken = default)
        => Task.FromResult(_skills.GetValueOrDefault(skillId));

    public Task<IReadOnlyList<Skill>> ListByWorkspaceAsync(Guid workspaceId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<Skill>>(_skills.Values.Where(s => s.WorkspaceId == workspaceId).OrderBy(s => s.CreatedAt).ToArray());
}

public sealed class InMemoryAccountGroupRepository : IAccountGroupRepository
{
    private readonly ConcurrentDictionary<Guid, AccountGroup> _groups = new();
    private readonly ConcurrentDictionary<(Guid GroupId, Guid UserId), GroupMembership> _memberships = new();

    public Task<AccountGroup> SaveAsync(AccountGroup group, CancellationToken cancellationToken = default)
    {
        _groups[group.Id] = group;
        return Task.FromResult(group);
    }

    public Task<IReadOnlyList<AccountGroup>> ListByAccountAsync(Guid accountId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<AccountGroup>>(_groups.Values.Where(g => g.AccountId == accountId).OrderBy(g => g.CreatedAt).ToArray());

    public Task<GroupMembership> AddMemberAsync(GroupMembership membership, CancellationToken cancellationToken = default)
    {
        _memberships[(membership.GroupId, membership.UserId)] = membership;
        return Task.FromResult(membership);
    }

    public Task RemoveMemberAsync(Guid groupId, Guid userId, CancellationToken cancellationToken = default)
    {
        _memberships.TryRemove((groupId, userId), out _);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Guid>> ListGroupIdsForUserAsync(Guid userId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<Guid>>(_memberships.Values.Where(m => m.UserId == userId).Select(m => m.GroupId).Distinct().ToArray());
}

public sealed class InMemoryAllowedDomainRepository : IAllowedDomainRepository
{
    private readonly ConcurrentDictionary<Guid, AllowedDomain> _domains = new();

    public Task<AllowedDomain> SaveAsync(AllowedDomain domain, CancellationToken cancellationToken = default)
    {
        _domains[domain.Id] = domain;
        return Task.FromResult(domain);
    }

    public Task<IReadOnlyList<AllowedDomain>> ListByAccountAsync(Guid accountId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<AllowedDomain>>(_domains.Values.Where(d => d.AccountId == accountId).OrderBy(d => d.CreatedAt).ToArray());

    public Task DeleteAsync(Guid domainId, CancellationToken cancellationToken = default)
    {
        _domains.TryRemove(domainId, out _);
        return Task.CompletedTask;
    }
}
