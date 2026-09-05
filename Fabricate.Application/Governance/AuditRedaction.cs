using System.Text.RegularExpressions;

namespace Fabricate.Application.Governance;

/// <summary>
/// Scrubs an audit event's free-form <c>Details</c> before it leaves the system (#74).
///
/// <para>
/// Details is written by a dozen call sites in whatever shape each found convenient — <c>k=v;k=v</c> in most,
/// JSON in some. An export is a file that leaves the building, so it cannot assume every past and future call
/// site got redaction right. This redacts by key name rather than by value shape, because a value that merely
/// looks harmless can still be sensitive once its key says what it is.
/// </para>
/// </summary>
public static partial class AuditRedaction
{
    public const string Placeholder = "[redacted]";

    /// <summary>
    /// Key names whose value is never safe to export. <c>fingerprint</c> is included deliberately: it is a hash
    /// prefix of a live credential, and correlating it across accounts tells an attacker which tenants share a
    /// key. <c>connection</c> catches connection strings, which carry embedded passwords.
    /// </summary>
    [GeneratedRegex(
        @"(?<key>\b(?:secret|password|passwd|pwd|token|api[-_]?key|apikey|access[-_]?key|private[-_]?key|credential|fingerprint|connection[-_]?string|connectionstring|conn[-_]?str|dsn)\b)(?<sep>\s*[=:]\s*""?)(?<value>[^;,""}\r\n]*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveAssignment();

    /// <summary>
    /// Provider key shapes that can appear without a key name at all — pasted into a free-text detail, say.
    /// Anchored on the vendor prefixes rather than on "long random string", which would eat correlation ids.
    /// </summary>
    [GeneratedRegex(
        @"\b(?:sk-[A-Za-z0-9_\-]{16,}|xox[abposr]-[A-Za-z0-9\-]{10,}|AKIA[0-9A-Z]{16}|ghp_[A-Za-z0-9]{20,}|AIza[0-9A-Za-z_\-]{20,})",
        RegexOptions.CultureInvariant)]
    private static partial Regex BareSecret();

    /// <summary>
    /// A full ADO-style connection string, which carries credentials even when no single key matched — for
    /// example when it was recorded under a neutral name like <c>target=</c>.
    /// </summary>
    [GeneratedRegex(
        @"\b(?:Server|Host|Data Source)\s*=\s*[^;""]*;(?:[^;""]*;)*?\s*(?:Password|Pwd)\s*=\s*[^;""]*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ConnectionString();

    /// <summary>Returns <paramref name="details"/> with every sensitive value replaced. Null stays null.</summary>
    public static string? Redact(string? details)
    {
        if (string.IsNullOrEmpty(details)) return details;

        var redacted = ConnectionString().Replace(details, Placeholder);
        redacted = SensitiveAssignment().Replace(
            redacted,
            static match => match.Groups["key"].Value + match.Groups["sep"].Value + Placeholder);
        return BareSecret().Replace(redacted, Placeholder);
    }
}
