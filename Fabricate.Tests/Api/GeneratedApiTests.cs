using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fabricate.Api;
using FluentAssertions;

namespace Fabricate.Tests.Api;

/// <summary>
/// #70: <c>OpenApiContractIngestionService</c> parsed a document into endpoints that nothing stored and nothing
/// served. This walks the whole path the issue describes — ingest a contract, run a generation, bind both
/// operations to its table, call the mock routes — and checks the refusals as carefully as the successes.
/// </summary>
[Collection("ApiIntegration")]
public sealed class GeneratedApiTests
{
    private static readonly Guid Account = StartupBootstrapService.BootstrapAccountId;

    private static FabricateApiFactory NewFactory()
        => new(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["FABRICATE_API_USAGE_SAMPLING"] = "0",
            ["FABRICATE_ARTIFACTS_PATH"] = Path.Combine(Path.GetTempPath(), $"fabricate-genapi-{Guid.NewGuid():N}"),
        });

    /// <summary>A contract with the list and item operations the acceptance criteria name.</summary>
    private const string Contract = """
        {
          "openapi": "3.0.0",
          "info": { "title": "Customers", "version": "1.4.0" },
          "paths": {
            "/customers": {
              "get": {
                "operationId": "listCustomers",
                "responses": {
                  "200": {
                    "description": "ok",
                    "content": {
                      "application/json": {
                        "schema": {
                          "type": "array",
                          "items": {
                            "type": "object",
                            "required": ["id", "email"],
                            "properties": {
                              "id": { "type": "integer" },
                              "email": { "type": "string" }
                            }
                          }
                        }
                      }
                    }
                  }
                }
              }
            },
            "/customers/{id}": {
              "get": {
                "operationId": "getCustomer",
                "responses": {
                  "200": {
                    "description": "ok",
                    "content": {
                      "application/json": {
                        "schema": {
                          "type": "object",
                          "required": ["id", "email"],
                          "properties": {
                            "id": { "type": "integer" },
                            "email": { "type": "string" }
                          }
                        }
                      }
                    }
                  }
                }
              }
            }
          }
        }
        """;

    private static object CustomerSchema() => new
    {
        name = "fixture",
        tables = new object[]
        {
            new
            {
                schema = "main",
                name = "customers",
                columns = new object[]
                {
                    new { name = "id", sqlType = "INTEGER", dataKind = "Integer", isNullable = false, isPrimaryKey = true, isUnique = true, maxLength = (int?)null, precision = (int?)null, scale = (int?)null, defaultExpression = (string?)null },
                    new { name = "email", sqlType = "TEXT", dataKind = "Email", isNullable = false, isPrimaryKey = false, isUnique = true, maxLength = (int?)200, precision = (int?)null, scale = (int?)null, defaultExpression = (string?)null },
                },
                primaryKey = new[] { "id" },
                foreignKeys = Array.Empty<object>(),
                uniqueConstraints = Array.Empty<object>(),
                indexes = Array.Empty<object>(),
            },
        },
    };

    private static async Task<Guid> CreateWorkspaceAsync(HttpClient client, string name)
    {
        using var response = await client.PostAsJsonAsync(new Uri("/workspaces", UriKind.Relative), new { accountId = Account, name });
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<Guid> RunAsync(HttpClient client, Guid workspaceId)
    {
        using var snapshot = await client.PostAsJsonAsync(
            new Uri($"/workspaces/{workspaceId}/schema-snapshots", UriKind.Relative), new { schema = CustomerSchema() });
        using var snapshotDocument = JsonDocument.Parse(await snapshot.Content.ReadAsStringAsync());

        using var run = await client.PostAsJsonAsync(
            new Uri($"/workspaces/{workspaceId}/runs", UriKind.Relative),
            new
            {
                rowCounts = new Dictionary<string, int> { ["main.customers"] = 5 },
                seed = 4242L,
                schemaSnapshotId = snapshotDocument.RootElement.GetProperty("id").GetGuid(),
                exporters = new[] { "json" },
            });

        run.StatusCode.Should().Be(HttpStatusCode.Created);
        using var runDocument = JsonDocument.Parse(await run.Content.ReadAsStringAsync());
        return runDocument.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement[]> IngestAsync(HttpClient client, Guid workspaceId)
    {
        using var ingested = await client.PostAsJsonAsync(
            new Uri($"/workspaces/{workspaceId}/api-contracts", UriKind.Relative),
            new { name = "customers", document = Contract });
        ingested.StatusCode.Should().Be(HttpStatusCode.Created);

        using var endpoints = await client.GetAsync(new Uri($"/workspaces/{workspaceId}/api-endpoints", UriKind.Relative));
        using var document = JsonDocument.Parse(await endpoints.Content.ReadAsStringAsync());
        return document.RootElement.EnumerateArray().Select(e => e.Clone()).ToArray();
    }

    private static Task<HttpResponseMessage> BindAsync(HttpClient client, Guid workspaceId, Guid endpointId, Guid runId, string table)
        => client.PatchAsJsonAsync(
            new Uri($"/workspaces/{workspaceId}/api-endpoints/{endpointId}", UriKind.Relative),
            new { artifactRunId = runId, boundTable = table });

    [Fact]
    public async Task IngestingAContractStoresItsEndpointsWithTheirResponseShape()
    {
        using var factory = NewFactory();
        using var client = factory.CreateAuthenticatedClient();
        var workspaceId = await CreateWorkspaceAsync(client, "contracts");

        var endpoints = await IngestAsync(client, workspaceId);

        endpoints.Should().HaveCount(2);
        endpoints.Select(e => e.GetProperty("operationId").GetString())
            .Should().BeEquivalentTo(["listCustomers", "getCustomer"]);

        var list = endpoints.Single(e => e.GetProperty("operationId").GetString() == "listCustomers");
        var item = endpoints.Single(e => e.GetProperty("operationId").GetString() == "getCustomer");

        list.GetProperty("responseKind").GetString().Should().Be("Collection");
        item.GetProperty("responseKind").GetString().Should().Be("Item",
            "an operation whose response is an object, on a path ending in a parameter, serves one row");
        list.GetProperty("isServable").GetBoolean().Should().BeFalse("nothing is bound yet");

        using var contracts = await client.GetAsync(new Uri($"/workspaces/{workspaceId}/api-contracts", UriKind.Relative));
        using var contractsDocument = JsonDocument.Parse(await contracts.Content.ReadAsStringAsync());
        contractsDocument.RootElement.EnumerateArray().Should().ContainSingle()
            .Which.GetProperty("version").GetString().Should().Be("1.4.0");
    }

    [Fact]
    public async Task BoundEndpointsServeContractValidJson()
    {
        using var factory = NewFactory();
        using var client = factory.CreateAuthenticatedClient();
        var workspaceId = await CreateWorkspaceAsync(client, "serving");
        var runId = await RunAsync(client, workspaceId);
        var endpoints = await IngestAsync(client, workspaceId);

        foreach (var endpoint in endpoints)
        {
            using var bound = await BindAsync(client, workspaceId, endpoint.GetProperty("id").GetGuid(), runId, "main.customers");
            bound.StatusCode.Should().Be(HttpStatusCode.OK);

            using var boundDocument = JsonDocument.Parse(await bound.Content.ReadAsStringAsync());
            boundDocument.RootElement.GetProperty("diagnostics").ValueKind.Should().Be(JsonValueKind.Null,
                "the table satisfies the contract, so there is nothing to report");
            boundDocument.RootElement.GetProperty("isServable").GetBoolean().Should().BeTrue();
        }

        // The list operation returns every row, as an array.
        using var list = await client.GetAsync(new Uri($"/workspaces/{workspaceId}/mock/customers", UriKind.Relative));
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        list.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        list.Headers.GetValues("X-Fabricate-Operation").Should().ContainSingle().Which.Should().Be("listCustomers");

        using var listDocument = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        listDocument.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
        listDocument.RootElement.GetArrayLength().Should().Be(5);

        var first = listDocument.RootElement[0];
        first.GetProperty("id").ValueKind.Should().Be(JsonValueKind.Number, "the contract declares an integer");
        first.GetProperty("email").ValueKind.Should().Be(JsonValueKind.String);

        // The item operation returns the one row matching the path parameter.
        var wantedId = first.GetProperty("id").GetInt64();
        using var item = await client.GetAsync(new Uri($"/workspaces/{workspaceId}/mock/customers/{wantedId}", UriKind.Relative));
        item.StatusCode.Should().Be(HttpStatusCode.OK);
        item.Headers.GetValues("X-Fabricate-Operation").Should().ContainSingle().Which.Should().Be("getCustomer");

        using var itemDocument = JsonDocument.Parse(await item.Content.ReadAsStringAsync());
        itemDocument.RootElement.ValueKind.Should().Be(JsonValueKind.Object, "an item operation returns one object");
        itemDocument.RootElement.GetProperty("id").GetInt64().Should().Be(wantedId);
    }

    [Fact]
    public async Task AnUnboundEndpointIsNotFound()
    {
        using var factory = NewFactory();
        using var client = factory.CreateAuthenticatedClient();
        var workspaceId = await CreateWorkspaceAsync(client, "unbound");
        await IngestAsync(client, workspaceId);

        using var response = await client.GetAsync(new Uri($"/workspaces/{workspaceId}/mock/customers", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "an endpoint with nothing behind it has nothing to serve");
    }

    [Fact]
    public async Task AnInactiveEndpointIsNotFound()
    {
        using var factory = NewFactory();
        using var client = factory.CreateAuthenticatedClient();
        var workspaceId = await CreateWorkspaceAsync(client, "inactive");
        var runId = await RunAsync(client, workspaceId);
        var endpoints = await IngestAsync(client, workspaceId);
        var list = endpoints.Single(e => e.GetProperty("operationId").GetString() == "listCustomers").GetProperty("id").GetGuid();

        await BindAsync(client, workspaceId, list, runId, "main.customers");
        (await client.GetAsync(new Uri($"/workspaces/{workspaceId}/mock/customers", UriKind.Relative))).StatusCode
            .Should().Be(HttpStatusCode.OK);

        using var deactivated = await client.PatchAsJsonAsync(
            new Uri($"/workspaces/{workspaceId}/api-endpoints/{list}", UriKind.Relative), new { isActive = false });
        deactivated.StatusCode.Should().Be(HttpStatusCode.OK);

        (await client.GetAsync(new Uri($"/workspaces/{workspaceId}/mock/customers", UriKind.Relative))).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task APathOutsideTheContractIsNotFound()
    {
        using var factory = NewFactory();
        using var client = factory.CreateAuthenticatedClient();
        var workspaceId = await CreateWorkspaceAsync(client, "outside");
        var runId = await RunAsync(client, workspaceId);
        var endpoints = await IngestAsync(client, workspaceId);

        foreach (var endpoint in endpoints)
        {
            await BindAsync(client, workspaceId, endpoint.GetProperty("id").GetGuid(), runId, "main.customers");
        }

        (await client.GetAsync(new Uri($"/workspaces/{workspaceId}/mock/invoices", UriKind.Relative))).StatusCode
            .Should().Be(HttpStatusCode.NotFound);

        // A method the contract does not declare for a path it does.
        using var wrongMethod = await client.PostAsJsonAsync(
            new Uri($"/workspaces/{workspaceId}/mock/customers", UriKind.Relative), new { });
        wrongMethod.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AnItemPathWithNoMatchingRowIsNotFound()
    {
        using var factory = NewFactory();
        using var client = factory.CreateAuthenticatedClient();
        var workspaceId = await CreateWorkspaceAsync(client, "missing-row");
        var runId = await RunAsync(client, workspaceId);
        var endpoints = await IngestAsync(client, workspaceId);
        var item = endpoints.Single(e => e.GetProperty("operationId").GetString() == "getCustomer").GetProperty("id").GetGuid();

        await BindAsync(client, workspaceId, item, runId, "main.customers");

        (await client.GetAsync(new Uri($"/workspaces/{workspaceId}/mock/customers/999999999", UriKind.Relative)))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task BindingToATableThatDoesNotSatisfyTheContractIsReportedAndNotServed()
    {
        using var factory = NewFactory();
        using var client = factory.CreateAuthenticatedClient();
        var workspaceId = await CreateWorkspaceAsync(client, "mismatch");
        var runId = await RunAsync(client, workspaceId);
        var endpoints = await IngestAsync(client, workspaceId);
        var list = endpoints.Single(e => e.GetProperty("operationId").GetString() == "listCustomers").GetProperty("id").GetGuid();

        using var bound = await BindAsync(client, workspaceId, list, runId, "main.no_such_table");
        bound.StatusCode.Should().Be(HttpStatusCode.OK, "a bad binding is a fact about the endpoint, not a failed request");

        using var boundDocument = JsonDocument.Parse(await bound.Content.ReadAsStringAsync());
        boundDocument.RootElement.GetProperty("diagnostics").GetString()
            .Should().Contain("main.no_such_table", "the diagnostic has to say what is wrong to be actionable");
        boundDocument.RootElement.GetProperty("isServable").GetBoolean().Should().BeFalse();

        (await client.GetAsync(new Uri($"/workspaces/{workspaceId}/mock/customers", UriKind.Relative))).StatusCode
            .Should().Be(HttpStatusCode.NotFound, "an endpoint with a diagnostic is not served");
    }

    [Fact]
    public async Task TheMockRoutesRequireAuthentication()
    {
        using var factory = NewFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri($"/workspaces/{Guid.NewGuid()}/mock/customers", UriKind.Relative));

        response.StatusCode.Should().BeOneOf(
            [HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden],
            "a mock endpoint is still this instance serving a tenant's data");
    }

    [Fact]
    public async Task AnInvalidContractIsRejectedWithItsParseErrors()
    {
        using var factory = NewFactory();
        using var client = factory.CreateAuthenticatedClient();
        var workspaceId = await CreateWorkspaceAsync(client, "invalid");

        using var response = await client.PostAsJsonAsync(
            new Uri($"/workspaces/{workspaceId}/api-contracts", UriKind.Relative),
            new { name = "broken", document = "{\"this\":\"is not an openapi document\"}" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
