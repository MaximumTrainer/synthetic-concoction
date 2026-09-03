using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;

namespace Fabricate.Application.Workflows;

public sealed class WorkflowService(IAuditLogService auditLogService) : IWorkflowService
{
    private readonly List<Workflow> _workflows = [];
    private readonly List<WorkflowStep> _steps = [];
    private readonly List<WorkflowRun> _runs = [];
    private readonly List<WorkflowStepRun> _stepRuns = [];

    public async Task<Workflow> CreateAsync(CreateWorkflowCommand command, Guid requestingUserId, CancellationToken cancellationToken = default)
    {
        var workflow = new Workflow(Guid.NewGuid(), command.WorkspaceId, command.Name, 1, WorkflowStatus.Active, DateTimeOffset.UtcNow);
        _workflows.Add(workflow);

        foreach (var stepDef in command.Steps.OrderBy(s => s.StepOrder))
        {
            _steps.Add(new WorkflowStep(Guid.NewGuid(), workflow.Id, stepDef.StepOrder, stepDef.StepType, stepDef.Configuration));
        }

        await auditLogService.RecordAsync(new AuditEvent(
            Guid.NewGuid(), command.WorkspaceId, requestingUserId,
            "workflow.created", "Workflow", workflow.Id.ToString(),
            Guid.NewGuid().ToString(), DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);

        return workflow;
    }

    public async Task<WorkflowRun> RunAsync(Guid workflowId, Guid requestingUserId, CancellationToken cancellationToken = default)
    {
        var workflow = _workflows.Find(w => w.Id == workflowId)
            ?? throw new InvalidOperationException($"Workflow '{workflowId}' not found.");

        if (workflow.Status == WorkflowStatus.Disabled)
        {
            throw new InvalidOperationException($"Workflow '{workflowId}' is disabled.");
        }

        var run = new WorkflowRun(Guid.NewGuid(), workflowId, WorkflowRunStatus.Queued, DateTimeOffset.UtcNow);
        _runs.Add(run);

        var workflowSteps = _steps.Where(s => s.WorkflowId == workflowId).OrderBy(s => s.StepOrder).ToArray();
        foreach (var step in workflowSteps)
        {
            _stepRuns.Add(new WorkflowStepRun(Guid.NewGuid(), run.Id, step.Id, step.StepOrder, WorkflowRunStatus.Queued, 0));
        }

        return run;
    }

    public Task<WorkflowRun?> GetRunAsync(Guid runId, CancellationToken cancellationToken = default)
        => Task.FromResult(_runs.Find(r => r.Id == runId));

    public Task<IReadOnlyList<WorkflowStepRun>> GetStepRunsAsync(Guid runId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<WorkflowStepRun>>(_stepRuns.Where(sr => sr.WorkflowRunId == runId).OrderBy(sr => sr.StepOrder).ToArray());

    public async Task<Workflow> DisableAsync(Guid workflowId, Guid requestingUserId, CancellationToken cancellationToken = default)
    {
        var index = _workflows.FindIndex(w => w.Id == workflowId);
        if (index < 0) throw new InvalidOperationException($"Workflow '{workflowId}' not found.");

        var disabled = _workflows[index] with { Status = WorkflowStatus.Disabled };
        _workflows[index] = disabled;

        await auditLogService.RecordAsync(new AuditEvent(
            Guid.NewGuid(), disabled.WorkspaceId, requestingUserId,
            "workflow.disabled", "Workflow", workflowId.ToString(),
            Guid.NewGuid().ToString(), DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);

        return disabled;
    }
}
