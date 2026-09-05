using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Fabricate.Application.Workspaces;

/// <summary>
/// Reduces a connection string to something safe to store alongside it, show, and put in an error message (#69).
///
/// <para>
/// A connection string contains a password by construction, so the whole value can never leave the instance. What
/// callers actually need is to recognise <em>which</em> connection they are looking at — which host, which
/// database — and that survives redaction. A driver's own exception messages routinely quote the connection
/// string back, so <see cref="Scrub"/> exists to clean those before they reach a response or a log.
/// </para>
/// </summary>
public static partial class ConnectionStringRedactor
{
    public const string Placeholder = "***";

    /// <summary>Keys whose values are credentials in the common ADO and URI connection-string dialects.</summary>
    [GeneratedRegex(
        @"(?<key>\b(?:password|pwd|user id|username|uid|user|api[-_ ]?key|access[-_ ]?key|secret|token|shared access signature|sas|accountkey)\s*=\s*)(?<value>[^;]*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KeyValueCredential();

    /// <summary>The <c>user:password@host</c> form used by URI-style connection strings.</summary>
    [GeneratedRegex(
        @"(?<scheme>[a-z][a-z0-9+.-]*://)(?<userinfo>[^/@\s]+)@",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UriUserInfo();

    /// <summary>
    /// A form safe to display and store: credential values replaced, everything else — host, port, database,
    /// SSL mode — kept, because that is what makes one connection recognisable from another.
    /// </summary>
    public static string Redact(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return string.Empty;

        var redacted = UriUserInfo().Replace(connectionString, m => m.Groups["scheme"].Value + Placeholder + "@");
        return KeyValueCredential().Replace(redacted, m => m.Groups["key"].Value + Placeholder);
    }

    /// <summary>
    /// Removes any occurrence of the connection string, or its credential parts, from arbitrary text. Used on
    /// driver exception messages, which quote the connection string back more often than not.
    /// </summary>
    public static string Scrub(string? text, string? connectionString)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(connectionString)) return text;

        var scrubbed = text.Replace(connectionString, Placeholder, StringComparison.OrdinalIgnoreCase);

        // Also remove each credential value on its own: a driver may echo only the password, not the whole string.
        foreach (Match match in KeyValueCredential().Matches(connectionString))
        {
            var value = match.Groups["value"].Value.Trim();
            if (value.Length >= 4)
            {
                scrubbed = scrubbed.Replace(value, Placeholder, StringComparison.Ordinal);
            }
        }

        foreach (Match match in UriUserInfo().Matches(connectionString))
        {
            var userInfo = match.Groups["userinfo"].Value;
            if (userInfo.Length >= 4)
            {
                scrubbed = scrubbed.Replace(userInfo, Placeholder, StringComparison.Ordinal);
            }
        }

        return Redact(scrubbed);
    }

    /// <summary>
    /// A short, stable hash. Enough to tell two connections apart and to see that a rotation happened, without
    /// being reversible or usable.
    /// </summary>
    public static string Fingerprint(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return string.Empty;

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(connectionString));
        return Convert.ToHexStringLower(hash)[..12];
    }
}
