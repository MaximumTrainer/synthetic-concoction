using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;

namespace Fabricate.Application.Accounts;

public sealed class InvitationService(
    IUserRepository userRepository,
    IAccountRepository accountRepository,
    IAllowedDomainService allowedDomainService,
    IAuditLogService auditLogService) : IInvitationService
{
    public async Task<Invitation> InviteAsync(InviteUserCommand command, CancellationToken cancellationToken = default)
    {
        var membership = await accountRepository.GetMembershipAsync(command.AccountId, command.InvitedByUserId, cancellationToken).ConfigureAwait(false);
        if (membership?.Role != AccountRole.Owner)
        {
            throw new UnauthorizedAccessException("Only account owners may invite users.");
        }

        var allowed = await allowedDomainService.IsEmailAllowedAsync(command.AccountId, command.InviteeEmail, cancellationToken).ConfigureAwait(false);
        if (!allowed)
        {
            throw new InvalidOperationException($"Email domain for '{command.InviteeEmail}' is not in the allowed-domains list for this account.");
        }

        var token = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var invitation = new Invitation(
            Guid.NewGuid(),
            command.AccountId,
            command.InvitedByUserId,
            command.InviteeEmail,
            token,
            DateTimeOffset.UtcNow.Add(command.Expiry),
            DateTimeOffset.UtcNow);

        await userRepository.SaveInvitationAsync(invitation, cancellationToken).ConfigureAwait(false);

        await auditLogService.RecordAsync(new AuditEvent(
            Guid.NewGuid(), command.AccountId, command.InvitedByUserId,
            "invitation.created", "Invitation", invitation.Id.ToString(),
            Guid.NewGuid().ToString(), DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);

        return invitation;
    }

    public async Task<AccountMembership> AcceptAsync(AcceptInvitationCommand command, CancellationToken cancellationToken = default)
    {
        var invitation = await userRepository.GetInvitationByTokenAsync(command.Token, cancellationToken).ConfigureAwait(false);

        if (invitation is null)
        {
            throw new InvalidOperationException("Invitation token is invalid.");
        }
        if (invitation.IsExpired)
        {
            throw new InvalidOperationException("Invitation has expired.");
        }
        if (invitation.IsRevoked)
        {
            throw new InvalidOperationException("Invitation has been revoked.");
        }
        if (invitation.IsAccepted)
        {
            throw new InvalidOperationException("Invitation has already been accepted.");
        }

        var accepted = invitation with { AcceptedAt = DateTimeOffset.UtcNow };
        await userRepository.SaveInvitationAsync(accepted, cancellationToken).ConfigureAwait(false);

        var membership = new AccountMembership(invitation.AccountId, command.UserId, AccountRole.Member, DateTimeOffset.UtcNow);
        await accountRepository.AddMemberAsync(membership, cancellationToken).ConfigureAwait(false);

        await auditLogService.RecordAsync(new AuditEvent(
            Guid.NewGuid(), invitation.AccountId, command.UserId,
            "invitation.accepted", "Invitation", invitation.Id.ToString(),
            Guid.NewGuid().ToString(), DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);

        return membership;
    }

    public async Task RevokeAsync(RevokeInvitationCommand command, CancellationToken cancellationToken = default)
    {
        var invitation = await userRepository.GetInvitationByIdAsync(command.InvitationId, cancellationToken).ConfigureAwait(false);
        if (invitation is null)
        {
            throw new InvalidOperationException("Invitation not found.");
        }

        var membership = await accountRepository.GetMembershipAsync(invitation.AccountId, command.RequestingUserId, cancellationToken).ConfigureAwait(false);
        if (membership?.Role != AccountRole.Owner)
        {
            throw new UnauthorizedAccessException("Only account owners may revoke invitations.");
        }

        var revoked = invitation with { RevokedAt = DateTimeOffset.UtcNow };
        await userRepository.SaveInvitationAsync(revoked, cancellationToken).ConfigureAwait(false);

        await auditLogService.RecordAsync(new AuditEvent(
            Guid.NewGuid(), invitation.AccountId, command.RequestingUserId,
            "invitation.revoked", "Invitation", invitation.Id.ToString(),
            Guid.NewGuid().ToString(), DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
    }
}
