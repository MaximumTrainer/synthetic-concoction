using System.Net;
using System.Net.Sockets;

namespace Fabricate.Application.Llm;

/// <summary>
/// Egress allowlist for tenant-supplied endpoints. An attacker-controlled base URL is a credential-exfiltration
/// and SSRF primitive, so anything that is not a public HTTPS host is rejected unless the operator opted in.
/// </summary>
public static class LlmEndpointPolicy
{
    public static Uri Validate(string endpoint, IReadOnlyList<string> allowedHosts, bool allowPrivateEndpoints)
    {
        if (string.IsNullOrWhiteSpace(endpoint) || !Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri))
        {
            throw new ArgumentException("Endpoint must be an absolute URL.", nameof(endpoint));
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ArgumentException("Endpoint must not embed credentials.", nameof(endpoint));
        }

        var isHttps = string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        var isHttp = string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);

        if (!isHttps && !(isHttp && allowPrivateEndpoints))
        {
            throw new ArgumentException("Endpoint must use https://.", nameof(endpoint));
        }

        if (!allowPrivateEndpoints && IsPrivateHost(uri))
        {
            throw new ArgumentException("Endpoint must be a public host; loopback, private and link-local addresses are not allowed.", nameof(endpoint));
        }

        if (allowedHosts.Count > 0 && !allowedHosts.Any(h => HostMatches(uri.Host, h)))
        {
            throw new ArgumentException($"Endpoint host '{uri.Host}' is not in the allowed endpoint hosts.", nameof(endpoint));
        }

        return uri;
    }

    private static bool HostMatches(string host, string allowed)
    {
        allowed = allowed.Trim().TrimStart('.');
        return host.Equals(allowed, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("." + allowed, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPrivateHost(Uri uri)
    {
        var host = uri.Host;

        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase)
            || !host.Contains('.'))
        {
            return true;
        }

        if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6 && IPAddress.TryParse(host.Trim('[', ']'), out var ip))
        {
            return IsPrivateAddress(ip);
        }

        return false;
    }

    private static bool IsPrivateAddress(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any))
        {
            return true;
        }

        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
                || b[0] == 0;
        }

        return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal;
    }
}
