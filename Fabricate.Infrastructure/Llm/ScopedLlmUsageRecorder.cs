using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Fabricate.Infrastructure.Llm;

/// <summary>
/// Writes usage records through a scope of its own (#77).
///
/// <para>
/// <see cref="ChatCompletionClientFactory"/> is a singleton, and with a database provider configured the usage
/// repository is scoped — injecting it directly would capture one DbContext for the process, which is the defect
/// #78 was raised for. A record is also written from inside a streaming enumeration, which can outlive the
/// request scope, so borrowing the ambient one would be wrong even if the lifetimes matched.
/// </para>
/// </summary>
public sealed class ScopedLlmUsageRecorder(IServiceScopeFactory scopeFactory) : ILlmUsageRecorder
{
    public async Task RecordAsync(LlmUsageRecord record, CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<ILlmUsageRepository>();
        await repository.RecordAsync(record, cancellationToken).ConfigureAwait(false);
    }
}
