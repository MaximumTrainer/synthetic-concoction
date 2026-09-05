using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;

namespace Fabricate.Application.Llm;

/// <summary>
/// Reads usage back and answers the budget question (#77). Attribution was the missing half: every turn already
/// recorded its token counts and every call was logged, but nothing aggregated either, so "what is this workspace
/// spending" and "has it spent too much" had no answer.
/// </summary>
public sealed class LlmUsageService(
    ILlmUsageRepository usageRepository,
    IWorkspaceService workspaceService,
    IWorkspaceRepository workspaceRepository,
    IAccountRepository accountRepository,
    ILlmCredentialStore policyStore,
    TimeProvider? timeProvider = null) : ILlmUsageService
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>Usage queries default to the last 30 days when no window is given.</summary>
    private static readonly TimeSpan DefaultWindow = TimeSpan.FromDays(30);

    public async Task<LlmUsageSummary> GetWorkspaceUsageAsync(
        Guid workspaceId,
        Guid requestingUserId,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        LlmUsageGrouping groupBy = LlmUsageGrouping.Model,
        CancellationToken cancellationToken = default)
    {
        // Any workspace member may read its usage: it is their own consumption, and hiding it from the people
        // doing the work is how a budget becomes a surprise.
        var role = await workspaceService.GetEffectiveRoleAsync(workspaceId, requestingUserId, cancellationToken).ConfigureAwait(false);
        if (role is null)
        {
            throw new UnauthorizedAccessException("You do not have access to this workspace.");
        }

        var (start, end) = ResolveWindow(from, to);
        return await usageRepository.SummariseWorkspaceAsync(workspaceId, start, end, groupBy, cancellationToken).ConfigureAwait(false);
    }

    public async Task<LlmUsageSummary> GetAccountUsageAsync(
        Guid accountId,
        Guid requestingUserId,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        LlmUsageGrouping groupBy = LlmUsageGrouping.Model,
        CancellationToken cancellationToken = default)
    {
        // The account rollup spans workspaces the caller may not individually belong to, so it is owners only.
        var membership = await accountRepository.GetMembershipAsync(accountId, requestingUserId, cancellationToken).ConfigureAwait(false);
        if (membership?.Role != AccountRole.Owner)
        {
            throw new UnauthorizedAccessException("Only account owners can read account-wide LLM usage.");
        }

        var workspaces = await workspaceRepository.ListByAccountAsync(accountId, cancellationToken).ConfigureAwait(false);
        var (start, end) = ResolveWindow(from, to);

        return await usageRepository
            .SummariseWorkspacesAsync(workspaces.Select(w => w.Id).ToArray(), start, end, groupBy, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<LlmBudgetVerdict> CheckBudgetAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        var policy = await policyStore.GetPolicyAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        if (policy is null || (policy.DailyTokenBudget is null && policy.MonthlyTokenBudget is null))
        {
            return LlmBudgetVerdict.Allowed;
        }

        var now = _time.GetUtcNow();

        // Daily first: it is the tighter window and the cheaper query, and the message is more actionable.
        if (policy.DailyTokenBudget is long daily)
        {
            var since = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
            var used = await usageRepository.TotalTokensAsync(workspaceId, since, now.AddTicks(1), cancellationToken).ConfigureAwait(false);
            if (used >= daily)
            {
                return new LlmBudgetVerdict(false,
                    $"This workspace has used {used:N0} of its {daily:N0} token daily budget. " +
                    "The budget resets at 00:00 UTC; a workspace admin can raise or clear it on the LLM policy.");
            }
        }

        if (policy.MonthlyTokenBudget is long monthly)
        {
            var since = new DateTimeOffset(new DateTime(now.UtcDateTime.Year, now.UtcDateTime.Month, 1), TimeSpan.Zero);
            var used = await usageRepository.TotalTokensAsync(workspaceId, since, now.AddTicks(1), cancellationToken).ConfigureAwait(false);
            if (used >= monthly)
            {
                return new LlmBudgetVerdict(false,
                    $"This workspace has used {used:N0} of its {monthly:N0} token monthly budget. " +
                    "The budget resets at 00:00 UTC on the first of the month; a workspace admin can raise or clear it.");
            }
        }

        return LlmBudgetVerdict.Allowed;
    }

    private (DateTimeOffset From, DateTimeOffset To) ResolveWindow(DateTimeOffset? from, DateTimeOffset? to)
    {
        var end = to ?? _time.GetUtcNow();
        return (from ?? end - DefaultWindow, end);
    }
}
