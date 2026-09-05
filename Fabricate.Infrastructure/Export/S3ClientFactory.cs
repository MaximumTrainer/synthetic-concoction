using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Fabricate.Application.Abstractions;

namespace Fabricate.Infrastructure.Export;

/// <summary>
/// Builds the S3 client for the configured artifact store (#84).
///
/// <para>
/// Ambient cloud identity first — an IAM role on ECS or EKS, or the instance profile — because on a cloud target
/// that means no key is stored anywhere at all. Explicit keys are the fallback for MinIO, R2 and Backblaze, which
/// have no ambient identity, and they are read through <see cref="ISecretProvider"/> rather than from the
/// environment directly so they follow the same path as every other secret.
/// </para>
/// </summary>
/// <remarks>Public so the MinIO integration tests build a client exactly as the composition root does.</remarks>
public static class S3ClientFactory
{
    public static IAmazonS3 Create(ArtifactStoreOptions options, ISecretProvider secrets)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(secrets);

        var config = new AmazonS3Config
        {
            ForcePathStyle = options.ForcePathStyle,
        };

        if (!string.IsNullOrWhiteSpace(options.ServiceUrl))
        {
            // A non-AWS endpoint: MinIO, Cloudflare R2, Backblaze B2. The region is still required by the
            // signing algorithm even where the store ignores it, so it falls back to a valid placeholder.
            config.ServiceURL = options.ServiceUrl;
            config.AuthenticationRegion = options.Region ?? "us-east-1";
        }
        else if (!string.IsNullOrWhiteSpace(options.Region))
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(options.Region);
        }

        if (string.IsNullOrWhiteSpace(options.AccessKeySecretName))
        {
            // No keys configured: let the SDK resolve ambient credentials.
            return new AmazonS3Client(config);
        }

        // Resolved synchronously: this runs once, while the container is being built.
        var accessKey = secrets.ResolveAsync(options.AccessKeySecretName).GetAwaiter().GetResult();
        var secretKey = secrets.ResolveAsync(options.SecretKeySecretName!).GetAwaiter().GetResult();

        return new AmazonS3Client(new BasicAWSCredentials(accessKey, secretKey), config);
    }
}
