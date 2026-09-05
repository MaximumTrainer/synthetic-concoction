using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;

namespace Fabricate.Application.Workflows;

public sealed class WorkflowService(
    IWorkflowRepository workflowRepository,
    IAuditLogService auditLogService) : IWorkflowService
{
    public async Task<Workflow> CreateAsync(CreateWorkflowCommand command, Guid requestingUserId, CancellationToken cancellationToken = default)
    {
        var workflow = new Workflow(Guid.NewGuid(), command.WorkspaceId, command.Name, 1, WorkflowStatus.Active, DateTimeOffset.UtcNow);
        await workflowRepository.SaveAsync(workflow, cancellationToken).ConfigureAwait(false);

        foreach (var stepDef in command.Steps.OrderBy(s => s.StepOrder))
        {
            await workflowRepository.SaveStepAsync(
                new WorkflowStep(Guid.NewGuid(), workflow.Id, stepDef.StepOrder, stepDef.StepType, stepDef.Configuration),
                cancellationToken).ConfigureAwait(false);
        }

        await auditLogService.RecordAsync(new AuditEvent(
            Guid.NewGuid(), command.WorkspaceId, requestingUserId,
            "workflow.created", "Workflow", workflow.Id.ToString(),
            Guid.NewGuid().ToString(), DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);

        return workflow;
    }

    public async Task<WorkflowRun> RunAsync(Guid workflowId, Guid requestingUserId, CancellationToken cancellationToken = default)
    {
        var workflow = await workflowRepository.GetByIdAsync(workflowId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Workflow '{workflowId}' not found.");

        if (workflow.Status == WorkflowStatus.Disabled)
        {
            throw new InvalidOperationException($"Workflow '{workflowId}' is disabled.");
        }

        var run = new WorkflowRun(Guid.NewGuid(), workflowId, WorkflowRunStatus.Queued, DateTimeOffset.UtcNow);
        await workflowRepository.SaveRunAsync(run, cancellationToken).ConfigureAwait(false);

        var steps = await workflowRepository.ListStepsAsync(workflowId, cancellationToken).ConfigureAwait(false);
        foreach (var step in steps)
        {
            await workflowRepository.SaveStepRunAsync(
                new WorkflowStepRun(Guid.NewGuid(), run.Id, step.Id, step.StepOrder, WorkflowRunStatus.Queued, 0),
                cancellationToken).ConfigureAwait(false);
        }

        return run;
    }

    public Task<WorkflowRun?> GetRunAsync(Guid runId, CancellationToken cancellationToken = default)
        => workflowRepository.GetRunAsync(runId, cancellationToken);

    public Task<IReadOnlyList<WorkflowStepRun>> GetStepRunsAsync(Guid runId, CancellationToken cancellationToken = default)
        => workflowRepository.ListStepRunsAsync(runId, cancellationToken);

    public async Task<Workflow> DisableAsync(Guid workflowId, Guid requestingUserId, CancellationToken cancellationToken = default)
    {
        var workflow = await workflowRepository.GetByIdAsync(workflowId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Workflow '{workflowId}' not found.");

        var disabled = workflow with { Status = WorkflowStatus.Disabled };
        await workflowRepository.SaveAsync(disabled, cancellationToken).ConfigureAwait(false);

        await auditLogService.RecordAsync(new AuditEvent(
            Guid.NewGuid(), disabled.WorkspaceId, requestingUserId,
            "workflow.disabled", "Workflow", workflowId.ToString(),
            Guid.NewGuid().ToString(), DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);

        return disabled;
    }
}
