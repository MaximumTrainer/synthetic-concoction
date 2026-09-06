namespace Fabricate.Infrastructure.Configuration;

/// <summary>
/// Where the Data Protection key ring lives, and what wraps it (#76).
///
/// <para>
/// The key ring is what decrypts every tenant LLM credential and connection secret. On a single machine with a
/// persistent disk the file system is fine. On Fly, Cloud Run, Container Apps or ECS it is not: the disk is
/// ephemeral or unshared, so a replaced machine loses the ring and every stored credential becomes permanently
/// undecryptable, and two instances generate different rings and cannot read each other's rows.
/// </para>
/// </summary>
public sealed class KeyRingOptions
{
    /// <summary>Where key ring XML is stored: <c>filesystem</c> (default) or <c>database</c>.</summary>
    public string KeyStore { get; set; } = "filesystem";

    /// <summary>Directory for the <c>filesystem</c> store. Ignored by the <c>database</c> store.</summary>
    public string? KeysPath { get; set; }

    /// <summary>
    /// Acknowledges storing an unwrapped key ring in the application database. Without a key-encryption key the
    /// ring sits beside the ciphertext it protects, so one database dump decrypts every tenant secret — which is
    /// a materially weaker position than the file-system store, not merely a different one.
    /// </summary>
    public bool AllowUnwrappedDatabaseKeyRing { get; set; }

    /// <summary>
    /// The key-encryption key that wraps the ring: <c>none</c> (default) or <c>aws-kms</c>. With one configured
    /// the database holds only wrapped keys, and unwrapping needs a KMS permission that a database dump does not
    /// carry — which restores the separation the file-system store had.
    /// </summary>
    public string Kek { get; set; } = "none";

    /// <summary>KMS key id, ARN or alias (e.g. <c>alias/fabricate-keyring</c>). Required with <c>aws-kms</c>.</summary>
    public string? KmsKeyId { get; set; }

    /// <summary>AWS region for KMS. Falls back to the ambient SDK configuration when unset.</summary>
    public string? KmsRegion { get; set; }

    /// <summary>Endpoint override, for LocalStack in tests. Ignored in production configurations.</summary>
    public string? KmsServiceUrl { get; set; }

    public bool UsesDatabase => string.Equals(KeyStore, "database", StringComparison.OrdinalIgnoreCase);

    public bool UsesAwsKms => string.Equals(Kek, "aws-kms", StringComparison.OrdinalIgnoreCase);

    public bool HasKek => !string.Equals(Kek, "none", StringComparison.OrdinalIgnoreCase);

    public static KeyRingOptions FromEnvironment(Func<string, string?> read) => new()
    {
        KeyStore = read("FABRICATE_DATA_PROTECTION_KEY_STORE")?.Trim() is { Length: > 0 } store ? store : "filesystem",
        KeysPath = read("FABRICATE_DATA_PROTECTION_KEYS_PATH"),
        AllowUnwrappedDatabaseKeyRing =
            string.Equals(read("FABRICATE_DATA_PROTECTION_ALLOW_UNWRAPPED"), "true", StringComparison.OrdinalIgnoreCase),
        Kek = read("FABRICATE_DATA_PROTECTION_KEK")?.Trim() is { Length: > 0 } kek ? kek : "none",
        KmsKeyId = read("FABRICATE_DATA_PROTECTION_KMS_KEY_ID"),
        KmsRegion = read("FABRICATE_DATA_PROTECTION_KMS_REGION"),
    };

    /// <summary>Configuration errors, refused at startup rather than surfacing as undecryptable rows later.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        var known = new[] { "filesystem", "database" };
        if (!known.Contains(KeyStore, StringComparer.OrdinalIgnoreCase))
        {
            errors.Add($"FABRICATE_DATA_PROTECTION_KEY_STORE must be one of {string.Join(", ", known)}; got '{KeyStore}'.");
        }

        var kekKinds = new[] { "none", "aws-kms" };
        if (!kekKinds.Contains(Kek, StringComparer.OrdinalIgnoreCase))
        {
            errors.Add($"FABRICATE_DATA_PROTECTION_KEK must be one of {string.Join(", ", kekKinds)}; got '{Kek}'.");
        }

        if (UsesAwsKms && string.IsNullOrWhiteSpace(KmsKeyId))
        {
            errors.Add("FABRICATE_DATA_PROTECTION_KMS_KEY_ID is required when FABRICATE_DATA_PROTECTION_KEK=aws-kms.");
        }

        // A KEK is the thing that makes the database store safe, so it lifts the acknowledgement rather than
        // sitting alongside it: with the ring wrapped, a database dump no longer carries the means to unwrap it.
        if (UsesDatabase && !HasKek && !AllowUnwrappedDatabaseKeyRing)
        {
            errors.Add(
                "FABRICATE_DATA_PROTECTION_KEY_STORE=database stores the key ring in the same database as the " +
                "secrets it protects, so a database dump alone decrypts every tenant credential. Configure a " +
                "key-encryption key (FABRICATE_DATA_PROTECTION_KEK=aws-kms), or set " +
                "FABRICATE_DATA_PROTECTION_ALLOW_UNWRAPPED=true to accept the risk, or keep the file-system store " +
                "on shared, persistent storage.");
        }

        return errors;
    }
}
