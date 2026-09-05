using System.Globalization;

namespace Fabricate.Infrastructure.Export;

/// <summary>Where generated artifacts are kept, and for how long (#84).</summary>
public sealed record ArtifactStoreOptions
{
    /// <summary>
    /// <c>filesystem</c> (default), <c>s3</c>, <c>azure-blob</c> or <c>gcs</c>. The filesystem stays the default
    /// because it needs no configuration and is right for local use — it is only wrong on a hosted target, where
    /// the container filesystem does not survive a restart.
    /// </summary>
    public string Kind { get; init; } = "filesystem";

    public string? BucketName { get; init; }

    /// <summary>
    /// Set for any S3-compatible store that is not AWS — MinIO, Cloudflare R2, Backblaze B2. Left unset, the
    /// SDK resolves the AWS endpoint for <see cref="Region"/>.
    /// </summary>
    public string? ServiceUrl { get; init; }

    public string? Region { get; init; }

    /// <summary>
    /// Required by MinIO and most S3-compatible stores, which do not support virtual-host-style addressing.
    /// </summary>
    public bool ForcePathStyle { get; init; }

    /// <summary>
    /// Names of secrets holding explicit keys. Left unset, the SDK's ambient credential chain is used — an IAM
    /// role on ECS or EKS, or the instance profile — which is the right answer on a cloud target and means no key
    /// is stored at all.
    /// </summary>
    public string? AccessKeySecretName { get; init; }

    public string? SecretKeySecretName { get; init; }

    /// <summary>Azure Blob: the storage account URL, e.g. <c>https://acme.blob.core.windows.net</c>.</summary>
    public string? AccountUrl { get; init; }

    /// <summary>
    /// Azure Blob: name of a secret holding a connection string. Left unset, managed identity is used against
    /// <see cref="AccountUrl"/> — which is the point of running on Azure, and stores no key at all.
    /// </summary>
    public string? ConnectionStringSecretName { get; init; }

    /// <summary>GCS: the project id. Optional — Application Default Credentials usually supply it.</summary>
    public string? ProjectId { get; init; }

    /// <summary>
    /// GCS: name of a secret holding service-account key JSON. Left unset, Application Default Credentials are
    /// used — the workload identity on GKE or Cloud Run, which again stores no key.
    /// </summary>
    public string? CredentialsJsonSecretName { get; init; }

    /// <summary>Days to keep artifacts. <c>0</c> — the default — keeps them, so upgrading changes nothing.</summary>
    public int RetentionDays { get; init; }

    public bool IsS3 => Kind.Equals("s3", StringComparison.OrdinalIgnoreCase);

    public bool IsAzureBlob => Kind.Equals("azure-blob", StringComparison.OrdinalIgnoreCase);

    public bool IsGcs => Kind.Equals("gcs", StringComparison.OrdinalIgnoreCase);

    public bool IsObjectStorage => IsS3 || IsAzureBlob || IsGcs;

    public bool RetentionEnabled => RetentionDays > 0;

    public static ArtifactStoreOptions FromEnvironment(Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);

        return new ArtifactStoreOptions
        {
            Kind = Trimmed(read("FABRICATE_ARTIFACT_STORE")) ?? "filesystem",
            BucketName = Trimmed(read("FABRICATE_ARTIFACT_BUCKET")),
            ServiceUrl = Trimmed(read("FABRICATE_ARTIFACT_S3_ENDPOINT")),
            Region = Trimmed(read("FABRICATE_ARTIFACT_S3_REGION")),
            ForcePathStyle = string.Equals(Trimmed(read("FABRICATE_ARTIFACT_S3_FORCE_PATH_STYLE")), "true", StringComparison.OrdinalIgnoreCase),
            AccessKeySecretName = Trimmed(read("FABRICATE_ARTIFACT_S3_ACCESS_KEY_SECRET")),
            SecretKeySecretName = Trimmed(read("FABRICATE_ARTIFACT_S3_SECRET_KEY_SECRET")),
            AccountUrl = Trimmed(read("FABRICATE_ARTIFACT_AZURE_ACCOUNT_URL")),
            ConnectionStringSecretName = Trimmed(read("FABRICATE_ARTIFACT_AZURE_CONNECTION_STRING_SECRET")),
            ProjectId = Trimmed(read("FABRICATE_ARTIFACT_GCS_PROJECT_ID")),
            CredentialsJsonSecretName = Trimmed(read("FABRICATE_ARTIFACT_GCS_CREDENTIALS_SECRET")),
            RetentionDays = int.TryParse(read("FABRICATE_ARTIFACT_RETENTION_DAYS"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var days) && days > 0
                ? days
                : 0,
        };
    }

    /// <summary>Configuration problems worth refusing to start over, rather than failing on the first run.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (!Kind.Equals("filesystem", StringComparison.OrdinalIgnoreCase) && !IsObjectStorage)
        {
            errors.Add($"FABRICATE_ARTIFACT_STORE '{Kind}' is not supported. Use 'filesystem', 's3', 'azure-blob' or 'gcs'.");
        }

        if (IsObjectStorage && string.IsNullOrWhiteSpace(BucketName))
        {
            errors.Add($"FABRICATE_ARTIFACT_BUCKET is required when FABRICATE_ARTIFACT_STORE is '{Kind}'. " +
                       "For azure-blob it names the container.");
        }

        if (IsS3 && string.IsNullOrWhiteSpace(ServiceUrl) && string.IsNullOrWhiteSpace(Region))
        {
            errors.Add("Set FABRICATE_ARTIFACT_S3_REGION (for AWS) or FABRICATE_ARTIFACT_S3_ENDPOINT (for MinIO, R2 or B2).");
        }

        if (IsAzureBlob && string.IsNullOrWhiteSpace(AccountUrl) && string.IsNullOrWhiteSpace(ConnectionStringSecretName))
        {
            errors.Add("Set FABRICATE_ARTIFACT_AZURE_ACCOUNT_URL (to use managed identity) or " +
                       "FABRICATE_ARTIFACT_AZURE_CONNECTION_STRING_SECRET.");
        }

        // One key without the other is a misconfiguration that would silently fall through to ambient
        // credentials and fail later with an unrelated permissions error.
        if (string.IsNullOrWhiteSpace(AccessKeySecretName) != string.IsNullOrWhiteSpace(SecretKeySecretName))
        {
            errors.Add("Set both FABRICATE_ARTIFACT_S3_ACCESS_KEY_SECRET and FABRICATE_ARTIFACT_S3_SECRET_KEY_SECRET, or neither to use ambient cloud identity.");
        }

        return errors;
    }

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
