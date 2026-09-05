using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Fabricate.Api;
using FluentAssertions;

namespace Fabricate.Tests.Api;

/// <summary>
/// #66: <c>GET /runs</c> listed every run in the instance to any authenticated caller, no route started a run,
/// and artifacts had no download path at all. These cover the workspace scoping, the round trip from starting a
/// run to downloading its files, and the manifest.
/// </summary>
[Collection("ApiIntegration")]
public sealed class RunApiTests
{
    private static readonly Guid Account = StartupBootstrapService.BootstrapAccountId;

    private static FabricateApiFactory NewFactory()
        => new(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["FABRICATE_API_USAGE_SAMPLING"] = "0",
            // Artifacts land in a directory of this test's own, not the shared temp root.
            ["FABRICATE_ARTIFACTS_PATH"] = Path.Combine(Path.GetTempPath(), $"fabricate-artifacts-{Guid.NewGuid():N}"),
        });

    private static async Task<Guid> CreateWorkspaceAsync(HttpClient client, string name)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/workspaces", UriKind.Relative), new { accountId = Account, name });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetGuid();
    }

    /// <summary>
    /// The two-table schema runs are generated from. Captured as a snapshot rather than discovered, which is the
    /// realistic path — a run should be reproducible from stored inputs (#75) — and the only one available in a
    /// test host with no source database configured.
    /// </summary>
    private static object FixtureSchema() => new
    {
        name = "fixture",
        tables = new object[]
        {
            new
            {
                schema = "main",
                name = "users",
                columns = new object[]
                {
                    new { name = "id", sqlType = "INTEGER", dataKind = "Integer", isNullable = false, isPrimaryKey = true, isUnique = true, maxLength = (int?)null, precision = (int?)null, scale = (int?)null, defaultExpression = (string?)null },
                    new { name = "email", sqlType = "TEXT", dataKind = "Email", isNullable = false, isPrimaryKey = false, isUnique = true, maxLength = (int?)200, precision = (int?)null, scale = (int?)null, defaultExpression = (string?)null },
                    new { name = "display_name", sqlType = "TEXT", dataKind = "String", isNullable = false, isPrimaryKey = false, isUnique = false, maxLength = (int?)60, precision = (int?)null, scale = (int?)null, defaultExpression = (string?)null },
                },
                primaryKey = new[] { "id" },
                foreignKeys = Array.Empty<object>(),
                uniqueConstraints = new object[] { new { name = "uq_users_email", columns = new[] { "email" } } },
                indexes = Array.Empty<object>(),
            },
        },
    };

    private static async Task<Guid> CaptureSchemaSnapshotAsync(HttpClient client, Guid workspaceId)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri($"/workspaces/{workspaceId}/schema-snapshots", UriKind.Relative),
            new { schema = FixtureSchema() });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<HttpResponseMessage> StartRunAsync(HttpClient client, Guid workspaceId, long seed = 5150)
    {
        var snapshotId = await CaptureSchemaSnapshotAsync(client, workspaceId);
        return await client.PostAsJsonAsync(
            new Uri($"/workspaces/{workspaceId}/runs", UriKind.Relative),
            new
            {
                rowCounts = new Dictionary<string, int> { ["main.users"] = 5 },
                seed,
                schemaSnapshotId = snapshotId,
                exporters = new[] { "csv", "json" },
            });
    }

    [Fact]
    public async Task StartingARunProducesArtifactsWithAManifest()
    {
        using var factory = NewFactory();
        using var client = factory.CreateAuthenticatedClient();
        var workspaceId = await CreateWorkspaceAsync(client, "runs");

        using var started = await StartRunAsync(client, workspaceId);
        started.StatusCode.Should().Be(HttpStatusCode.Created);

        using var startedDocument = JsonDocument.Parse(await started.Content.ReadAsStringAsync());
        var runId = startedDocument.RootElement.GetProperty("id").GetGuid();
        startedDocument.RootElement.GetProperty("status").GetString().Should().Be("Completed");
        startedDocument.RootElement.GetProperty("workspaceId").GetGuid().Should().Be(workspaceId);

        using var manifest = await client.GetAsync(new Uri($"/workspaces/{workspaceId}/runs/{runId}/artifacts", UriKind.Relative));
        manifest.StatusCode.Should().Be(HttpStatusCode.OK);

        using var manifestDocument = JsonDocument.Parse(await manifest.Content.ReadAsStringAsync());
        var artifacts = manifestDocument.RootElement.EnumerateArray().ToArray();

        artifacts.Should().NotBeEmpty();
        artifacts.Select(a => a.GetProperty("name").GetString()).Should().Contain("summary.json");
        artifacts.Should().Contain(a => a.GetProperty("name").GetString()!.StartsWith("csv/", StringComparison.Ordinal));
        artifacts.Should().Contain(a => a.GetProperty("name").GetString()!.StartsWith("json/", StringComparison.Ordinal));

        foreach (var artifact in artifacts)
        {
            artifact.GetProperty("sizeBytes").GetInt64().Should().BeGreaterThan(0);
            artifact.GetProperty("sha256").GetString().Should().MatchRegex("^[0-9a-f]{64}$");
            artifact.GetProperty("contentType").GetString().Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public async Task ADownloadedArtifactMatchesItsChecksumAndContentType()
    {
        using var factory = NewFactory();
        using var client = factory.CreateAuthenticatedClient();
        var workspaceId = await CreateWorkspaceAsync(client, "download");

        using var started = await StartRunAsync(client, workspaceId);
        using var startedDocument = JsonDocument.Parse(await started.Content.ReadAsStringAsync());
        var runId = startedDocument.RootElement.GetProperty("id").GetGuid();

        using var manifest = await client.GetAsync(new Uri($"/workspaces/{workspaceId}/runs/{runId}/artifacts", UriKind.Relative));
        using var manifestDocument = JsonDocument.Parse(await manifest.Content.ReadAsStringAsync());

        var csv = manifestDocument.RootElement.EnumerateArray()
            .First(a => a.GetProperty("name").GetString()!.EndsWith(".csv", StringComparison.Ordinal));
        var name = csv.GetProperty("name").GetString()!;

        using var download = await client.GetAsync(new Uri($"/workspaces/{workspaceId}/runs/{runId}/artifacts/{name}", UriKind.Relative));
        download.StatusCode.Should().Be(HttpStatusCode.OK);
        download.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");

        var bytes = await download.Content.ReadAsByteArrayAsync();
        Convert.ToHexStringLower(SHA256.HashData(bytes)).Should().Be(csv.GetProperty("sha256").GetString(),
            "a download that does not match its published checksum is worse than no checksum");
    }

    [Fact]
    public async Task RunsAreListedOnlyForTheirOwnWorkspace()
    {
        using var factory = NewFactory();
        using var client = factory.CreateAuthenticatedClient();
        var first = await CreateWorkspaceAsync(client, "first");
        var second = await CreateWorkspaceAsync(client, "second");

        using var started = await StartRunAsync(client, first);
        using var startedDocument = JsonDocument.Parse(await started.Content.ReadAsStringAsync());
        var runId = startedDocument.RootElement.GetProperty("id").GetGuid();

        using var listedInOwn = await client.GetAsync(new Uri($"/workspaces/{first}/runs", UriKind.Relative));
        using var ownDocument = JsonDocument.Parse(await listedInOwn.Content.ReadAsStringAsync());
        ownDocument.RootElement.GetArrayLength().Should().Be(1);

        using var listedInOther = await client.GetAsync(new Uri($"/workspaces/{second}/runs", UriKind.Relative));
        using var otherDocument = JsonDocument.Parse(await listedInOther.Content.ReadAsStringAsync());
        otherDocument.RootElement.GetArrayLength().Should().Be(0,
            "a run belongs to the workspace that started it");

        // Reading it through the wrong workspace is not found, not forbidden — a 403 would confirm the id exists.
        using var readThroughOther = await client.GetAsync(new Uri($"/workspaces/{second}/runs/{runId}", UriKind.Relative));
        readThroughOther.StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var artifactsThroughOther = await client.GetAsync(
            new Uri($"/workspaces/{second}/runs/{runId}/artifacts", UriKind.Relative));
        artifactsThroughOther.StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var downloadThroughOther = await client.GetAsync(
            new Uri($"/workspaces/{second}/runs/{runId}/artifacts/summary.json", UriKind.Relative));
        downloadThroughOther.StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var cancelThroughOther = await client.PostAsJsonAsync(
            new Uri($"/workspaces/{second}/runs/{runId}/cancel", UriKind.Relative), new { });
        cancelThroughOther.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ThereIsNoInstanceWideRunsRoute()
    {
        using var factory = NewFactory();
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.GetAsync(new Uri("/runs?page=1&pageSize=20", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "listing every run in the instance to any authenticated caller was the defect");
    }

    [Fact]
    public async Task TwoRunsWithTheSameSeedProduceIdenticalArtifacts()
    {
        using var factory = NewFactory();
        using var client = factory.CreateAuthenticatedClient();
        var workspaceId = await CreateWorkspaceAsync(client, "determinism");

        var first = await ChecksumsForRunAsync(client, workspaceId, seed: 4242);
        var second = await ChecksumsForRunAsync(client, workspaceId, seed: 4242);

        first.Should().NotBeEmpty();
        second.Should().BeEquivalentTo(first,
            "the API path uses the same orchestrator and exporters as the CLI, so the seed is the whole input");
    }

    private static async Task<Dictionary<string, string>> ChecksumsForRunAsync(HttpClient client, Guid workspaceId, long seed)
    {
        using var started = await StartRunAsync(client, workspaceId, seed);
        using var startedDocument = JsonDocument.Parse(await started.Content.ReadAsStringAsync());
        var runId = startedDocument.RootElement.GetProperty("id").GetGuid();

        using var manifest = await client.GetAsync(new Uri($"/workspaces/{workspaceId}/runs/{runId}/artifacts", UriKind.Relative));
        using var manifestDocument = JsonDocument.Parse(await manifest.Content.ReadAsStringAsync());

        return manifestDocument.RootElement.EnumerateArray()
            // summary.json carries the run's own timestamps, so it differs between runs by design.
            .Where(a => a.GetProperty("name").GetString() != "summary.json")
            .ToDictionary(
                a => a.GetProperty("name").GetString()!,
                a => a.GetProperty("sha256").GetString()!,
                StringComparer.Ordinal);
    }

    [Fact]
    public async Task StartingARunRequiresAuthentication()
    {
        using var factory = NewFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            new Uri($"/workspaces/{Guid.NewGuid()}/runs", UriKind.Relative),
            new { rowCounts = new Dictionary<string, int>(), seed = 1L });

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AnUnknownExporterIsRejected()
    {
        using var factory = NewFactory();
        using var client = factory.CreateAuthenticatedClient();
        var workspaceId = await CreateWorkspaceAsync(client, "bad-exporter");
        var snapshotId = await CaptureSchemaSnapshotAsync(client, workspaceId);

        using var response = await client.PostAsJsonAsync(
            new Uri($"/workspaces/{workspaceId}/runs", UriKind.Relative),
            new
            {
                rowCounts = new Dictionary<string, int> { ["main.users"] = 1 },
                seed = 1L,
                schemaSnapshotId = snapshotId,
                exporters = new[] { "hieroglyphics" },
            });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Available:",
            "the error should say what the caller could have asked for");
    }

    [Fact]
    public async Task ARunNamingATableTheSchemaDoesNotHaveIsRejected()
    {
        using var factory = NewFactory();
        using var client = factory.CreateAuthenticatedClient();
        var workspaceId = await CreateWorkspaceAsync(client, "unknown-table");
        var snapshotId = await CaptureSchemaSnapshotAsync(client, workspaceId);

        using var response = await client.PostAsJsonAsync(
            new Uri($"/workspaces/{workspaceId}/runs", UriKind.Relative),
            new
            {
                rowCounts = new Dictionary<string, int> { ["main.no_such_table"] = 1 },
                seed = 1L,
                schemaSnapshotId = snapshotId,
            });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "a run that generated nothing would otherwise report success with an empty manifest");
        (await response.Content.ReadAsStringAsync()).Should().Contain("main.no_such_table");
    }
}
