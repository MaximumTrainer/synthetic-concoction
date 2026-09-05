using System.Globalization;

namespace Fabricate.Infrastructure.Export;

/// <summary>Where generated artifacts are kept, and for how long (#84).</summary>
public sealed record ArtifactStoreOptions
{
    /// <summary>
    /// <c>filesystem</c> (default) or <c>s3</c>. The filesystem stays the default because it needs no
    /// configuration and is right for local use — it is only wrong on a hosted target, where the container
    /// filesystem does not survive a restart.
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

    /// <summary>Days to keep artifacts. <c>0</c> — the default — keeps them, so upgrading changes nothing.</summary>
    public int RetentionDays { get; init; }

    public bool IsObjectStorage => Kind.Equals("s3", StringComparison.OrdinalIgnoreCase);

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
            errors.Add($"FABRICATE_ARTIFACT_STORE '{Kind}' is not supported. Use 'filesystem' or 's3'.");
        }

        if (IsObjectStorage && string.IsNullOrWhiteSpace(BucketName))
        {
            errors.Add("FABRICATE_ARTIFACT_BUCKET is required when FABRICATE_ARTIFACT_STORE is 's3'.");
        }

        if (IsObjectStorage && string.IsNullOrWhiteSpace(ServiceUrl) && string.IsNullOrWhiteSpace(Region))
        {
            errors.Add("Set FABRICATE_ARTIFACT_S3_REGION (for AWS) or FABRICATE_ARTIFACT_S3_ENDPOINT (for MinIO, R2 or B2).");
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
