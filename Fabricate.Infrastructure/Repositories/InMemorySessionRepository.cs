using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;

namespace Fabricate.Infrastructure.Repositories;

public sealed class InMemorySessionRepository : ISessionRepository
{
    private readonly List<ChatSession> _sessions = [];
    private readonly List<ChatMessage> _messages = [];
    private readonly List<ToolInvocation> _invocations = [];
    private readonly object _lock = new();

    public Task<ChatSession> SaveAsync(ChatSession session, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _sessions.RemoveAll(s => s.Id == session.Id);
            _sessions.Add(session);
        }
        return Task.FromResult(session);
    }

    public Task<ChatSession?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            return Task.FromResult(_sessions.Find(s => s.Id == id));
        }
    }

    public Task<ChatMessage> SaveMessageAsync(ChatMessage message, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _messages.RemoveAll(m => m.Id == message.Id);
            _messages.Add(message);
        }
        return Task.FromResult(message);
    }

    public Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(Guid sessionId, int skip, int take, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var result = _messages.Where(m => m.SessionId == sessionId).OrderBy(m => m.CreatedAt).Skip(skip).Take(take).ToArray();
            return Task.FromResult<IReadOnlyList<ChatMessage>>(result);
        }
    }

    public Task<ToolInvocation> SaveInvocationAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _invocations.RemoveAll(i => i.Id == invocation.Id);
            _invocations.Add(invocation);
        }
        return Task.FromResult(invocation);
    }

    public Task<ToolInvocation?> GetInvocationAsync(Guid invocationId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            return Task.FromResult(_invocations.Find(i => i.Id == invocationId));
        }
    }

    public Task<IReadOnlyList<ToolInvocation>> ListInvocationsAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var result = _invocations.Where(i => i.SessionId == sessionId).OrderBy(i => i.StartedAt).ToArray();
            return Task.FromResult<IReadOnlyList<ToolInvocation>>(result);
        }
    }
}
