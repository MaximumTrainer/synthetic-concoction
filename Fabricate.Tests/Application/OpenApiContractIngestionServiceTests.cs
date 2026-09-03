using Fabricate.Application.Workflows;
using FluentAssertions;

namespace Fabricate.Tests.Application;

public sealed class OpenApiContractIngestionServiceTests
{
    private readonly OpenApiContractIngestionService _service = new();
    private readonly Guid _workspaceId = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();

    private const string SampleContract = """
    {
      "openapi": "3.0.0",
      "info": { "title": "Sample", "version": "1.0" },
      "paths": {
        "/customers": {
          "get": { "operationId": "listCustomers", "responses": { "200": { "description": "ok" } } },
          "post": { "responses": { "201": { "description": "created" } } }
        },
        "/orders/{id}": {
          "get": { "operationId": "getOrder", "responses": { "200": { "description": "ok" } } }
        }
      }
    }
    """;

    [Fact]
    public async Task IngestAsync_ShouldReturnOneEndpointPerOperation()
    {
        var endpoints = await _service.IngestAsync(SampleContract, _workspaceId, _userId);

        endpoints.Should().HaveCount(3);
        endpoints.Should().OnlyContain(e => e.WorkspaceId == _workspaceId && e.IsActive);
        endpoints.Select(e => (e.Path, e.Method)).Should().BeEquivalentTo(new[]
        {
            ("/customers", "GET"),
            ("/customers", "POST"),
            ("/orders/{id}", "GET"),
        });
    }

    [Fact]
    public async Task IngestAsync_ShouldUseOperationIdWhenPresent()
    {
        var endpoints = await _service.IngestAsync(SampleContract, _workspaceId, _userId);

        endpoints.Should().ContainSingle(e => e.OperationId == "listCustomers");
        endpoints.Should().ContainSingle(e => e.OperationId == "getOrder");
    }

    [Fact]
    public async Task IngestAsync_ShouldSynthesiseOperationId_WhenContractOmitsIt()
    {
        var endpoints = await _service.IngestAsync(SampleContract, _workspaceId, _userId);

        var post = endpoints.Single(e => e.Method == "POST");
        post.OperationId.Should().Be("POST_customers");
    }

    [Fact]
    public async Task IngestAsync_ShouldReturnEmpty_ForContractWithNoPaths()
    {
        const string empty = """
        { "openapi": "3.0.0", "info": { "title": "Empty", "version": "1.0" }, "paths": {} }
        """;

        var endpoints = await _service.IngestAsync(empty, _workspaceId, _userId);

        endpoints.Should().BeEmpty();
    }

    [Fact]
    public async Task IngestAsync_ShouldThrow_ForMalformedContract()
    {
        var act = async () => await _service.IngestAsync("{ not valid openapi", _workspaceId, _userId);

        await act.Should().ThrowAsync<Exception>();
    }
}
