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

    public bool UsesDatabase => string.Equals(KeyStore, "database", StringComparison.OrdinalIgnoreCase);

    public static KeyRingOptions FromEnvironment(Func<string, string?> read) => new()
    {
        KeyStore = read("FABRICATE_DATA_PROTECTION_KEY_STORE")?.Trim() is { Length: > 0 } store ? store : "filesystem",
        KeysPath = read("FABRICATE_DATA_PROTECTION_KEYS_PATH"),
        AllowUnwrappedDatabaseKeyRing =
            string.Equals(read("FABRICATE_DATA_PROTECTION_ALLOW_UNWRAPPED"), "true", StringComparison.OrdinalIgnoreCase),
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

        // No KEK adapter exists yet, so the database store currently always needs the acknowledgement. When one
        // lands this becomes "unless a KEK is configured".
        if (UsesDatabase && !AllowUnwrappedDatabaseKeyRing)
        {
            errors.Add(
                "FABRICATE_DATA_PROTECTION_KEY_STORE=database stores the key ring in the same database as the " +
                "secrets it protects, so a database dump alone decrypts every tenant credential. Set " +
                "FABRICATE_DATA_PROTECTION_ALLOW_UNWRAPPED=true to accept that, or keep the file-system store on " +
                "shared, persistent storage.");
        }

        return errors;
    }
}
