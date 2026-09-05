using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;
using Microsoft.EntityFrameworkCore;

namespace Fabricate.Infrastructure.Persistence;

// EF Core adapters for the platform aggregates introduced in #65, plus the webhook adapter deferred on #43.
// Grouped in one file because they are uniform upsert/query pairs over a single DbSet each.

internal static class EfUpsert
{
    /// <summary>Insert or update by primary key, without tracking conflicts when the same id is saved twice in a scope.</summary>
    internal static async Task<T> SaveAsync<T>(FabricateDbContext db, DbSet<T> set, T entity, object[] key, CancellationToken cancellationToken)
        where T : class
    {
        var existing = await set.FindAsync(key, cancellationToken);
        if (existing is null) set.Add(entity);
        else db.Entry(existing).CurrentValues.SetValues(entity);
        await db.SaveChangesAsync(cancellationToken);
        return entity;
    }
}

public sealed class EfWorkspaceRepository(FabricateDbContext db) : IWorkspaceRepository
{
    public Task<Workspace> SaveAsync(Workspace workspace, CancellationToken cancellationToken = default)
        => EfUpsert.SaveAsync(db, db.Workspaces, workspace, [workspace.Id], cancellationToken);

    public Task<Workspace?> GetByIdAsync(Guid workspaceId, CancellationToken cancellationToken = default)
        => db.Workspaces.FindAsync([workspaceId], cancellationToken).AsTask();

    public async Task<IReadOnlyList<Workspace>> ListByAccountAsync(Guid accountId, CancellationToken cancellationToken = default)
        => await db.Workspaces.Where(w => w.AccountId == accountId).OrderBy(w => w.CreatedAt).ToListAsync(cancellationToken);

    public Task<WorkspaceMembership> SaveMembershipAsync(WorkspaceMembership membership, CancellationToken cancellationToken = default)
        => EfUpsert.SaveAsync(db, db.WorkspaceMemberships, membership,
            [membership.WorkspaceId, membership.PrincipalId, membership.IsGroup], cancellationToken);

    public async Task RemoveMembershipAsync(Guid workspaceId, Guid principalId, bool isGroup, CancellationToken cancellationToken = default)
    {
        var existing = await db.WorkspaceMemberships.FindAsync([workspaceId, principalId, isGroup], cancellationToken);
        if (existing is null) return;
        db.WorkspaceMemberships.Remove(existing);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<WorkspaceMembership>> ListMembershipsAsync(Guid workspaceId, CancellationToken cancellationToken = default)
        => await db.WorkspaceMemberships.Where(m => m.WorkspaceId == workspaceId).ToListAsync(cancellationToken);
}

public sealed class EfConnectionRepository(FabricateDbContext db) : IConnectionRepository
{
    public Task<Connection> SaveAsync(Connection connection, CancellationToken cancellationToken = default)
        => EfUpsert.SaveAsync(db, db.Connections, connection, [connection.Id], cancellationToken);

    public Task<Connection?> GetByIdAsync(Guid connectionId, CancellationToken cancellationToken = default)
        => db.Connections.FindAsync([connectionId], cancellationToken).AsTask();

    public async Task<IReadOnlyList<Connection>> ListByWorkspaceAsync(Guid workspaceId, CancellationToken cancellationToken = default)
        => await db.Connections.Where(c => c.WorkspaceId == workspaceId).OrderBy(c => c.CreatedAt).ToListAsync(cancellationToken);

    public async Task DeleteAsync(Guid connectionId, CancellationToken cancellationToken = default)
    {
        var existing = await db.Connections.FindAsync([connectionId], cancellationToken);
        if (existing is null) return;
        db.Connections.Remove(existing);
        await db.SaveChangesAsync(cancellationToken);
    }
}

public sealed class EfInstructionVersionRepository(FabricateDbContext db) : IInstructionVersionRepository
{
    public Task<InstructionVersion> SaveAsync(InstructionVersion version, CancellationToken cancellationToken = default)
        => EfUpsert.SaveAsync(db, db.InstructionVersions, version, [version.Id], cancellationToken);

    public async Task<IReadOnlyList<InstructionVersion>> ListByWorkspaceAsync(Guid workspaceId, CancellationToken cancellationToken = default)
        => await db.InstructionVersions.Where(v => v.ProjectId == null && v.WorkspaceId == workspaceId)
            .OrderByDescending(v => v.Version).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<InstructionVersion>> ListByProjectAsync(Guid projectId, CancellationToken cancellationToken = default)
        => await db.InstructionVersions.Where(v => v.ProjectId == projectId)
            .OrderByDescending(v => v.Version).ToListAsync(cancellationToken);
}

public sealed class EfProjectRepository(FabricateDbContext db) : IProjectRepository
{
    public Task<Project> SaveAsync(Project project, CancellationToken cancellationToken = default)
        => EfUpsert.SaveAsync(db, db.Projects, project, [project.Id], cancellationToken);

    public Task<Project?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => db.Projects.FindAsync([id], cancellationToken).AsTask();

    public async Task<IReadOnlyList<Project>> ListByWorkspaceAsync(Guid workspaceId, CancellationToken cancellationToken = default)
        => await db.Projects.Where(p => p.WorkspaceId == workspaceId).OrderBy(p => p.CreatedAt).ToListAsync(cancellationToken);
}

public sealed class EfProjectDatabaseRepository(FabricateDbContext db) : IProjectDatabaseRepository
{
    public Task<ProjectDatabase> SaveAsync(ProjectDatabase database, CancellationToken cancellationToken = default)
        => EfUpsert.SaveAsync(db, db.ProjectDatabases, database, [database.Id], cancellationToken);

    public Task<ProjectDatabase?> GetByIdAsync(Guid databaseId, CancellationToken cancellationToken = default)
        => db.ProjectDatabases.FindAsync([databaseId], cancellationToken).AsTask();

    public async Task<IReadOnlyList<ProjectDatabase>> ListByProjectAsync(Guid projectId, CancellationToken cancellationToken = default)
        => await db.ProjectDatabases.Where(d => d.ProjectId == projectId).OrderBy(d => d.CreatedAt).ToListAsync(cancellationToken);

    public async Task DeleteAsync(Guid databaseId, CancellationToken cancellationToken = default)
    {
        var existing = await db.ProjectDatabases.FindAsync([databaseId], cancellationToken);
        if (existing is null) return;
        db.ProjectDatabases.Remove(existing);
        await db.SaveChangesAsync(cancellationToken);
    }
}

public sealed class EfWorkflowRepository(FabricateDbContext db) : IWorkflowRepository
{
    public Task<Workflow> SaveAsync(Workflow workflow, CancellationToken cancellationToken = default)
        => EfUpsert.SaveAsync(db, db.Workflows, workflow, [workflow.Id], cancellationToken);

    public Task<Workflow?> GetByIdAsync(Guid workflowId, CancellationToken cancellationToken = default)
        => db.Workflows.FindAsync([workflowId], cancellationToken).AsTask();

    public async Task<IReadOnlyList<Workflow>> ListByWorkspaceAsync(Guid workspaceId, CancellationToken cancellationToken = default)
        => await db.Workflows.Where(w => w.WorkspaceId == workspaceId).OrderBy(w => w.CreatedAt).ToListAsync(cancellationToken);

    public Task<WorkflowStep> SaveStepAsync(WorkflowStep step, CancellationToken cancellationToken = default)
        => EfUpsert.SaveAsync(db, db.WorkflowSteps, step, [step.Id], cancellationToken);

    public async Task<IReadOnlyList<WorkflowStep>> ListStepsAsync(Guid workflowId, CancellationToken cancellationToken = default)
        => await db.WorkflowSteps.Where(s => s.WorkflowId == workflowId).OrderBy(s => s.StepOrder).ToListAsync(cancellationToken);

    public Task<WorkflowRun> SaveRunAsync(WorkflowRun run, CancellationToken cancellationToken = default)
        => EfUpsert.SaveAsync(db, db.WorkflowRuns, run, [run.Id], cancellationToken);

    public Task<WorkflowRun?> GetRunAsync(Guid runId, CancellationToken cancellationToken = default)
        => db.WorkflowRuns.FindAsync([runId], cancellationToken).AsTask();

    public Task<WorkflowStepRun> SaveStepRunAsync(WorkflowStepRun stepRun, CancellationToken cancellationToken = default)
        => EfUpsert.SaveAsync(db, db.WorkflowStepRuns, stepRun, [stepRun.Id], cancellationToken);

    public async Task<IReadOnlyList<WorkflowStepRun>> ListStepRunsAsync(Guid runId, CancellationToken cancellationToken = default)
        => await db.WorkflowStepRuns.Where(sr => sr.WorkflowRunId == runId).OrderBy(sr => sr.StepOrder).ToListAsync(cancellationToken);
}

public sealed class EfSkillRepository(FabricateDbContext db) : ISkillRepository
{
    public Task<Skill> SaveAsync(Skill skill, CancellationToken cancellationToken = default)
        => EfUpsert.SaveAsync(db, db.Skills, skill, [skill.Id], cancellationToken);

    public Task<Skill?> GetByIdAsync(Guid skillId, CancellationToken cancellationToken = default)
        => db.Skills.FindAsync([skillId], cancellationToken).AsTask();

    public async Task<IReadOnlyList<Skill>> ListByWorkspaceAsync(Guid workspaceId, CancellationToken cancellationToken = default)
        => await db.Skills.Where(s => s.WorkspaceId == workspaceId).OrderBy(s => s.CreatedAt).ToListAsync(cancellationToken);
}

public sealed class EfAccountGroupRepository(FabricateDbContext db) : IAccountGroupRepository
{
    public Task<AccountGroup> SaveAsync(AccountGroup group, CancellationToken cancellationToken = default)
        => EfUpsert.SaveAsync(db, db.AccountGroups, group, [group.Id], cancellationToken);

    public async Task<IReadOnlyList<AccountGroup>> ListByAccountAsync(Guid accountId, CancellationToken cancellationToken = default)
        => await db.AccountGroups.Where(g => g.AccountId == accountId).OrderBy(g => g.CreatedAt).ToListAsync(cancellationToken);

    public Task<GroupMembership> AddMemberAsync(GroupMembership membership, CancellationToken cancellationToken = default)
        => EfUpsert.SaveAsync(db, db.GroupMemberships, membership, [membership.GroupId, membership.UserId], cancellationToken);

    public async Task RemoveMemberAsync(Guid groupId, Guid userId, CancellationToken cancellationToken = default)
    {
        var existing = await db.GroupMemberships.FindAsync([groupId, userId], cancellationToken);
        if (existing is null) return;
        db.GroupMemberships.Remove(existing);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> ListGroupIdsForUserAsync(Guid userId, CancellationToken cancellationToken = default)
        => await db.GroupMemberships.Where(m => m.UserId == userId).Select(m => m.GroupId).Distinct().ToListAsync(cancellationToken);
}

public sealed class EfAllowedDomainRepository(FabricateDbContext db) : IAllowedDomainRepository
{
    public Task<AllowedDomain> SaveAsync(AllowedDomain domain, CancellationToken cancellationToken = default)
        => EfUpsert.SaveAsync(db, db.AllowedDomains, domain, [domain.Id], cancellationToken);

    public async Task<IReadOnlyList<AllowedDomain>> ListByAccountAsync(Guid accountId, CancellationToken cancellationToken = default)
        => await db.AllowedDomains.Where(d => d.AccountId == accountId).OrderBy(d => d.CreatedAt).ToListAsync(cancellationToken);

    public async Task DeleteAsync(Guid domainId, CancellationToken cancellationToken = default)
    {
        var existing = await db.AllowedDomains.FindAsync([domainId], cancellationToken);
        if (existing is null) return;
        db.AllowedDomains.Remove(existing);
        await db.SaveChangesAsync(cancellationToken);
    }
}

public sealed class EfWebhookRepository(FabricateDbContext db) : IWebhookRepository
{
    public Task<WebhookRegistration> SaveAsync(WebhookRegistration webhook, CancellationToken cancellationToken = default)
        => EfUpsert.SaveAsync(db, db.WebhookRegistrations, webhook, [webhook.Id], cancellationToken);

    public Task<WebhookRegistration?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => db.WebhookRegistrations.FindAsync([id], cancellationToken).AsTask();

    public async Task<IReadOnlyList<WebhookRegistration>> ListByWorkspaceAsync(Guid workspaceId, CancellationToken cancellationToken = default)
        => await db.WebhookRegistrations.Where(w => w.WorkspaceId == workspaceId).OrderBy(w => w.CreatedAt).ToListAsync(cancellationToken);

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var existing = await db.WebhookRegistrations.FindAsync([id], cancellationToken);
        if (existing is null) return;
        db.WebhookRegistrations.Remove(existing);
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task<WebhookDelivery> SaveDeliveryAsync(WebhookDelivery delivery, CancellationToken cancellationToken = default)
        => EfUpsert.SaveAsync(db, db.WebhookDeliveries, delivery, [delivery.Id], cancellationToken);
}
