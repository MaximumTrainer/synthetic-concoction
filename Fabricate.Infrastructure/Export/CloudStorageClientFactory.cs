using Azure.Identity;
using Azure.Storage.Blobs;
using Fabricate.Application.Abstractions;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Storage.V1;

namespace Fabricate.Infrastructure.Export;

/// <summary>
/// Builds the Azure Blob and GCS clients for the configured artifact store (#90).
///
/// <para>
/// Ambient identity first in both cases — managed identity on Azure, Application Default Credentials on GCP —
/// because that is the whole reason to use the native store rather than pointing the S3 adapter at an
/// S3-compatible endpoint: no key is stored anywhere. Explicit credentials are the fallback for running outside
/// the cloud, and they hold the <em>name</em> of a secret so they follow the same path as every other secret.
/// </para>
/// </summary>
/// <remarks>Public so the emulator-backed integration tests build clients exactly as the composition root does.</remarks>
public static class CloudStorageClientFactory
{
    public static BlobContainerClient CreateAzureBlob(ArtifactStoreOptions options, ISecretProvider secrets)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(secrets);

        var service = string.IsNullOrWhiteSpace(options.ConnectionStringSecretName)
            // Managed identity against the account URL. DefaultAzureCredential also covers a developer signed in
            // locally, so the same configuration works on a workstation and on Container Apps.
            ? new BlobServiceClient(new Uri(options.AccountUrl!), new DefaultAzureCredential())
            : new BlobServiceClient(Resolve(secrets, options.ConnectionStringSecretName));

        return service.GetBlobContainerClient(options.BucketName!);
    }

    public static StorageClient CreateGcs(ArtifactStoreOptions options, ISecretProvider secrets)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(secrets);

        if (string.IsNullOrWhiteSpace(options.CredentialsJsonSecretName))
        {
            // Application Default Credentials: the workload identity on GKE or Cloud Run, or gcloud locally.
            return StorageClient.Create();
        }

        var json = Resolve(secrets, options.CredentialsJsonSecretName);
        return StorageClient.Create(GoogleCredential.FromJson(json));
    }

    /// <summary>
    /// Resolved synchronously because this runs once, while the container is being built — the alternative is an
    /// async factory registration that would make every consumer of IArtifactStore async for no benefit.
    /// </summary>
    private static string Resolve(ISecretProvider secrets, string secretName)
        => secrets.ResolveAsync(secretName).GetAwaiter().GetResult();
}
