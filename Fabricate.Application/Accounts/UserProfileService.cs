using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;

namespace Fabricate.Application.Accounts;

public sealed class UserProfileService(IUserRepository userRepository) : IUserProfileService
{
    public async Task<UserProfile> GetOrCreateAsync(Guid userId, string email, string displayName, CancellationToken cancellationToken = default)
    {
        var existing = await userRepository.GetByIdAsync(userId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        var profile = new UserProfile(userId, displayName, email, DateTimeOffset.UtcNow);
        return await userRepository.SaveAsync(profile, cancellationToken).ConfigureAwait(false);
    }

    public async Task<UserProfile> UpdateAsync(UpdateProfileCommand command, CancellationToken cancellationToken = default)
    {
        var existing = await userRepository.GetByIdAsync(command.UserId, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            throw new InvalidOperationException($"User profile '{command.UserId}' not found.");
        }

        var updated = existing with { DisplayName = command.DisplayName, UpdatedAt = DateTimeOffset.UtcNow };
        return await userRepository.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
    }

    public Task<UserProfile?> GetByIdAsync(Guid userId, CancellationToken cancellationToken = default)
        => userRepository.GetByIdAsync(userId, cancellationToken);
}
