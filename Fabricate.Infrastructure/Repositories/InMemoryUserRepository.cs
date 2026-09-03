using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;

namespace Fabricate.Infrastructure.Repositories;

public sealed class InMemoryUserRepository : IUserRepository
{
    private readonly List<UserProfile> _users = [];
    private readonly List<Invitation> _invitations = [];

    public Task<UserProfile> SaveAsync(UserProfile profile, CancellationToken cancellationToken = default)
    {
        _users.RemoveAll(u => u.UserId == profile.UserId);
        _users.Add(profile);
        return Task.FromResult(profile);
    }

    public Task<UserProfile?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => Task.FromResult(_users.Find(u => u.UserId == id));

    public Task<UserProfile?> GetByEmailAsync(string email, CancellationToken cancellationToken = default)
        => Task.FromResult(_users.Find(u => string.Equals(u.Email, email, StringComparison.OrdinalIgnoreCase)));

    public Task<Invitation> SaveInvitationAsync(Invitation invitation, CancellationToken cancellationToken = default)
    {
        _invitations.RemoveAll(i => i.Id == invitation.Id);
        _invitations.Add(invitation);
        return Task.FromResult(invitation);
    }

    public Task<Invitation?> GetInvitationByTokenAsync(string token, CancellationToken cancellationToken = default)
        => Task.FromResult(_invitations.Find(i => string.Equals(i.Token, token, StringComparison.OrdinalIgnoreCase)));

    public Task<Invitation?> GetInvitationByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => Task.FromResult(_invitations.Find(i => i.Id == id));
}
