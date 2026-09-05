using System.Text.Json;
using System.Text.RegularExpressions;
using Fabricate.Tests.Api;
using FluentAssertions;

namespace Fabricate.Tests.Api;

/// <summary>
/// #73: the TypeScript SDK called routes the API does not expose — <c>/projects</c>, <c>/workflows</c>,
/// <c>/api-keys</c> — and nothing caught it, because the SDK's own tests assert against a fake fetch and so
/// happily confirm a wrong URL. This reads the routes straight out of the SDK source and checks each one against
/// the OpenAPI document of the real, booted API, so drift on either side fails the build.
/// </summary>
[Collection("ApiIntegration")]
public sealed class SdkContractTests
{
    /// <summary>Matches `this.get&lt;T&gt;("/path")` and the template-literal form, capturing verb and path.</summary>
    private static readonly Regex HelperCall = new(
        """this\.(?<verb>get|post|put|patch|deleteFor|delete)\s*(?:<[^>]*>)?\s*\(\s*[`"](?<path>[^`"]+)[`"]""",
        RegexOptions.Compiled);

    /// <summary>
    /// Matches the one hand-rolled fetch (SSE streaming), which bypasses the helpers. The path must be a literal
    /// beginning with <c>/</c>: the helpers' own call sites interpolate the whole path from a variable, and those
    /// are call sites for routes already captured by <see cref="HelperCall"/>.
    /// </summary>
    private static readonly Regex DirectFetch = new(
        """"this\.fetchFn\(\s*`\$\{this\.baseUrl\}(?<path>/[^`]*)`\s*,\s*\{\s*method:\s*"(?<verb>[A-Z]+)"""",
        RegexOptions.Compiled);

    private static readonly Dictionary<string, string> VerbForHelper = new(StringComparer.Ordinal)
    {
        ["get"] = "GET",
        ["post"] = "POST",
        ["put"] = "PUT",
        ["patch"] = "PATCH",
        ["delete"] = "DELETE",
        ["deleteFor"] = "DELETE",
    };

    [Fact]
    public async Task EveryRouteTheTypeScriptSdkCalls_ExistsOnTheApiWithTheSameVerb()
    {
        var sdkRoutes = ReadSdkRoutes();
        sdkRoutes.Should().HaveCountGreaterThan(25, "the SDK covers accounts, workspaces, projects, runs, workflows, chat, credentials and API keys");

        var apiRoutes = await ReadApiRoutesAsync();
        apiRoutes.Should().NotBeEmpty("the API must publish an OpenAPI document for the contract check to work");

        var missing = sdkRoutes.Where(r => !apiRoutes.Contains(r)).OrderBy(r => r, StringComparer.Ordinal).ToArray();

        missing.Should().BeEmpty(
            "every SDK method must call a route the API exposes. Missing:\n  " +
            string.Join("\n  ", missing) +
            "\n\nAPI routes:\n  " +
            string.Join("\n  ", apiRoutes.OrderBy(r => r, StringComparer.Ordinal)));
    }

    /// <summary>
    /// The SDK's helpers are the only way it reaches the network, so anything that is not a helper call or the
    /// one direct fetch would slip past the check above. This pins that assumption.
    /// </summary>
    [Fact]
    public void TheSdkReachesTheNetworkOnlyThroughTheCheckedCallSites()
    {
        var source = File.ReadAllText(SdkClientPath());

        var fetchCalls = Regex.Matches(source, @"this\.fetchFn\(").Count;
        var helperImplementations = 3; // get, deleteFor and send each own one fetchFn call site

        fetchCalls.Should().Be(helperImplementations + DirectFetch.Matches(source).Count,
            "a fetch that is neither a helper implementation nor a recognised direct call would not be contract-checked");
    }

    private static string SdkClientPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "sdk", "typescript")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the contract check needs the SDK source from the repository");
        return Path.Combine(directory!.FullName, "sdk", "typescript", "src", "client.ts");
    }

    /// <summary>Every "VERB /path" the SDK can issue, with route parameters reduced to <c>{}</c>.</summary>
    private static HashSet<string> ReadSdkRoutes()
    {
        var source = File.ReadAllText(SdkClientPath());
        var routes = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match match in HelperCall.Matches(source))
        {
            // The helper implementations call fetchFn with the path they were given, so skip their own bodies.
            if (match.Groups["path"].Value.StartsWith("${", StringComparison.Ordinal)) continue;
            routes.Add($"{VerbForHelper[match.Groups["verb"].Value]} {Normalise(match.Groups["path"].Value)}");
        }

        foreach (Match match in DirectFetch.Matches(source))
        {
            routes.Add($"{match.Groups["verb"].Value} {Normalise(match.Groups["path"].Value)}");
        }

        return routes;
    }

    private static async Task<HashSet<string>> ReadApiRoutesAsync()
    {
        using var factory = new FabricateApiFactory();
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.GetAsync(new Uri("/swagger/v1/swagger.json", UriKind.Relative));
        response.IsSuccessStatusCode.Should().BeTrue($"the OpenAPI document must be served (got {(int)response.StatusCode})");

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var routes = new HashSet<string>(StringComparer.Ordinal);

        foreach (var path in document.RootElement.GetProperty("paths").EnumerateObject())
        {
            foreach (var operation in path.Value.EnumerateObject())
            {
                routes.Add($"{operation.Name.ToUpperInvariant()} {Normalise(path.Name)}");
            }
        }

        return routes;
    }

    /// <summary>
    /// Reduces a path to what both sides can agree on: no query string, no trailing slash (minimal API renders a
    /// group's <c>"/"</c> route with one), and every parameter — <c>${workspaceId}</c> in the SDK,
    /// <c>{workspaceId}</c> in OpenAPI — collapsed to <c>{}</c> so names need not match.
    /// </summary>
    private static string Normalise(string path)
    {
        var withoutQuery = path.Split('?')[0];
        var collapsed = Regex.Replace(withoutQuery, @"\$?\{[^}]*\}", "{}");
        var trimmed = collapsed.TrimEnd('/');
        return trimmed.Length == 0 ? "/" : trimmed;
    }
}
