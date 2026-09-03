using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;
using Microsoft.EntityFrameworkCore;

namespace Fabricate.Infrastructure.Persistence;

public sealed class EfUserRepository(FabricateDbContext db) : IUserRepository
{
    public async Task<UserProfile> SaveAsync(UserProfile profile, CancellationToken cancellationToken = default)
    {
        var existing = await db.UserProfiles.FindAsync([profile.UserId], cancellationToken);
        if (existing is null) db.UserProfiles.Add(profile);
        else db.Entry(existing).CurrentValues.SetValues(profile);
        await db.SaveChangesAsync(cancellationToken);
        return profile;
    }

    public Task<UserProfile?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => db.UserProfiles.FindAsync([id], cancellationToken).AsTask();

    public Task<UserProfile?> GetByEmailAsync(string email, CancellationToken cancellationToken = default)
        => db.UserProfiles.FirstOrDefaultAsync(u => u.Email == email, cancellationToken);

    public async Task<Invitation> SaveInvitationAsync(Invitation invitation, CancellationToken cancellationToken = default)
    {
        var existing = await db.Invitations.FindAsync([invitation.Id], cancellationToken);
        if (existing is null) db.Invitations.Add(invitation);
        else db.Entry(existing).CurrentValues.SetValues(invitation);
        await db.SaveChangesAsync(cancellationToken);
        return invitation;
    }

    public Task<Invitation?> GetInvitationByTokenAsync(string token, CancellationToken cancellationToken = default)
        => db.Invitations.FirstOrDefaultAsync(i => i.Token == token, cancellationToken);

    public Task<Invitation?> GetInvitationByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => db.Invitations.FindAsync([id], cancellationToken).AsTask();
}
