using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;
using Microsoft.EntityFrameworkCore;

namespace Fabricate.Infrastructure.Persistence;

public sealed class EfSessionRepository(FabricateDbContext db) : ISessionRepository
{
    public async Task<ChatSession> SaveAsync(ChatSession session, CancellationToken cancellationToken = default)
    {
        var existing = await db.ChatSessions.FindAsync([session.Id], cancellationToken);
        if (existing is null) db.ChatSessions.Add(session);
        else db.Entry(existing).CurrentValues.SetValues(session);
        await db.SaveChangesAsync(cancellationToken);
        return session;
    }

    public Task<ChatSession?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => db.ChatSessions.FindAsync([id], cancellationToken).AsTask();

    public async Task<ChatMessage> SaveMessageAsync(ChatMessage message, CancellationToken cancellationToken = default)
    {
        var existing = await db.ChatMessages.FindAsync([message.Id], cancellationToken);
        if (existing is null) db.ChatMessages.Add(message);
        else db.Entry(existing).CurrentValues.SetValues(message);
        await db.SaveChangesAsync(cancellationToken);
        return message;
    }

    public async Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(Guid sessionId, int skip, int take, CancellationToken cancellationToken = default)
        => await db.ChatMessages.Where(m => m.SessionId == sessionId)
            .OrderBy(m => m.Id).Skip(skip).Take(take).ToListAsync(cancellationToken);

    public async Task<ToolInvocation> SaveInvocationAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        var existing = await db.ToolInvocations.FindAsync([invocation.Id], cancellationToken);
        if (existing is null) db.ToolInvocations.Add(invocation);
        else db.Entry(existing).CurrentValues.SetValues(invocation);
        await db.SaveChangesAsync(cancellationToken);
        return invocation;
    }
}
