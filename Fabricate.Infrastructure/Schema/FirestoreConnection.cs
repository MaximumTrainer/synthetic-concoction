using Google.Api.Gax;
using Google.Cloud.Firestore;

namespace Fabricate.Infrastructure.Schema;

/// <summary>
/// Builds a Firestore client the same way for the discoverer and the profiler (#91), so the same
/// <c>--connection</c> means the same thing to <c>discover</c> and <c>discover-profile</c>.
/// </summary>
/// <remarks>
/// <see cref="EmulatorDetection.EmulatorOrProduction"/> is the setting Google's own guidance recommends: the
/// client uses the local emulator when <c>FIRESTORE_EMULATOR_HOST</c> is set and Application Default Credentials
/// otherwise, which is exactly the previous behaviour when the variable is absent. Without it the client asks for
/// ADC even when pointed at an emulator, so working against Firestore locally — or in a test — is impossible.
/// </remarks>
internal static class FirestoreConnection
{
    /// <summary>
    /// Resolves the project id from the connection string, falling back to <c>GOOGLE_CLOUD_PROJECT</c>.
    /// </summary>
    internal static string ResolveProjectId(string? connectionString)
        => !string.IsNullOrWhiteSpace(connectionString)
            ? connectionString.Trim()
            : Environment.GetEnvironmentVariable("GOOGLE_CLOUD_PROJECT")
              ?? throw new InvalidOperationException(
                  "GCP project ID must be provided as connectionString or GOOGLE_CLOUD_PROJECT must be set.");

    internal static Task<FirestoreDb> CreateAsync(
        string? connectionString,
        string? databaseName = null,
        CancellationToken cancellationToken = default)
    {
        var builder = new FirestoreDbBuilder
        {
            ProjectId = ResolveProjectId(connectionString),
            DatabaseId = string.IsNullOrWhiteSpace(databaseName) ? "(default)" : databaseName,
            EmulatorDetection = EmulatorDetection.EmulatorOrProduction,
        };

        return builder.BuildAsync(cancellationToken);
    }
}
