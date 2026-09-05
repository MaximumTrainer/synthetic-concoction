using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using Fabricate.Api;
using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Fabricate.Tests.Api;

/// <summary>
/// #72: nothing recorded which API key called which endpoint, so "API usage is auditable by key scope and
/// endpoint" (from #31) was unmet. These cover what is recorded, what is deliberately not, and the sampling switch.
/// </summary>
[Collection("ApiIntegration")]
public sealed class ApiUsageAuditTests
{
    private static readonly Guid Account = StartupBootstrapService.BootstrapAccountId;

    private static FabricateApiFactory NewFactory(string? sampling = null)
        => new(sampling is null
            ? null
            : new Dictionary<string, string?>(StringComparer.Ordinal) { ["FABRICATE_API_USAGE_SAMPLING"] = sampling });

    private static async Task<IReadOnlyList<AuditEvent>> UsageEventsAsync(FabricateApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IAuditLogRepository>();
        var page = await repository.QueryAsync(Account, new AuditFilter(ActionPrefix: "api."), 0, 100);
        return page;
    }

    [Fact]
    public async Task AnAuthenticatedRequestIsRecordedWithTheKeyAndRouteTemplate()
    {
        using var factory = NewFactory();
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.GetAsync(new Uri($"/accounts/{Account}", UriKind.Relative));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var usage = await UsageEventsAsync(factory);
        var record = usage.Should().ContainSingle(e => e.Action == "api.request").Subject;

        record.ApiKeyId.Should().NotBeNull("the usage record exists to answer \"what did this key do\"");
        record.AccountId.Should().Be(Account);
        record.TargetId.Should().Be("/accounts/{accountId:guid}", "the route template identifies the endpoint");
        record.Details.Should().Contain("method=GET").And.Contain("status=200").And.Contain("durationMs=");
        record.Details.Should().Contain("scopes=", "the key's scopes are part of what was asked for");
    }

    [Fact]
    public async Task TheRouteTemplateIsRecordedRatherThanThePath()
    {
        using var factory = NewFactory();
        using var client = factory.CreateAuthenticatedClient();
        var workspaceId = Guid.NewGuid();

        await client.GetAsync(new Uri($"/workspaces/{workspaceId}", UriKind.Relative));

        var usage = await UsageEventsAsync(factory);
        var record = usage.Should().ContainSingle().Subject;

        record.TargetId.Should().Be("/workspaces/{workspaceId:guid}");
        (record.Details + record.TargetId).Should().NotContain(workspaceId.ToString(),
            "a path carries tenant identifiers; the template says which endpoint was called without copying them " +
            "into a log that is exported and kept for months");
    }

    [Fact]
    public async Task AFailedRequestIsStillRecorded()
    {
        using var factory = NewFactory();
        using var client = factory.CreateAuthenticatedClient();

        // An account the bootstrap key does not own.
        using var response = await client.GetAsync(new Uri($"/accounts/{Guid.NewGuid()}/audit/export", UriKind.Relative));
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var usage = await UsageEventsAsync(factory);
        usage.Should().ContainSingle(e => e.Details!.Contains("status=403", StringComparison.Ordinal),
            "a refused request is exactly the kind whose usage record matters");
    }

    [Fact]
    public async Task AnonymousEndpointsAreNotRecorded()
    {
        using var factory = NewFactory();
        using var client = factory.CreateClient();

        using var health = await client.GetAsync(new Uri("/healthz", UriKind.Relative));
        health.StatusCode.Should().Be(HttpStatusCode.OK);

        (await UsageEventsAsync(factory)).Should().BeEmpty("/healthz has no key to attribute usage to");
    }

    [Fact]
    public async Task AnUnauthenticatedRequestIsNotRecorded()
    {
        using var factory = NewFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri($"/accounts/{Account}", UriKind.Relative));
        response.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);

        (await UsageEventsAsync(factory)).Should().BeEmpty(
            "a rejected key is an authentication event, not a usage record, and recording it would let an " +
            "unauthenticated caller write rows into an account's audit log");
    }

    [Fact]
    public async Task SamplingAtZeroRecordsNothing()
    {
        using var factory = NewFactory("0");
        using var client = factory.CreateAuthenticatedClient();

        await client.GetAsync(new Uri($"/accounts/{Account}", UriKind.Relative));

        (await UsageEventsAsync(factory)).Should().BeEmpty();
    }

    [Fact]
    public async Task UsageRecordsCarryNoHeadersQueryValuesOrBodies()
    {
        using var factory = NewFactory();
        using var client = factory.CreateAuthenticatedClient();
        client.DefaultRequestHeaders.Add("X-Custom-Trace", "header-value-should-not-be-logged");

        await client.GetAsync(new Uri($"/accounts/{Account}/audit?action=secret-query-value", UriKind.Relative));
        await client.PostAsJsonAsync(new Uri("/workspaces", UriKind.Relative),
            new { accountId = Account, name = "body-value-should-not-be-logged" });

        var usage = await UsageEventsAsync(factory);
        usage.Should().HaveCountGreaterThan(0);

        var everything = string.Join("\n", usage.Select(e => $"{e.Action}|{e.TargetId}|{e.Details}"));
        everything.Should().NotContain("header-value-should-not-be-logged");
        everything.Should().NotContain("secret-query-value");
        everything.Should().NotContain("body-value-should-not-be-logged");
        everything.Should().NotContain(FabricateApiFactory.BootstrapApiKey, "the key itself is never the record");
    }

    [Fact]
    public async Task TheAuditQueryFiltersByActionPrefixAndApiKey()
    {
        using var factory = NewFactory();
        using var client = factory.CreateAuthenticatedClient();

        await client.GetAsync(new Uri($"/accounts/{Account}", UriKind.Relative));

        var keyId = (await UsageEventsAsync(factory)).First().ApiKeyId!.Value;

        using var byPrefix = await client.GetAsync(
            new Uri($"/accounts/{Account}/audit?actionPrefix=api.&pageSize=100", UriKind.Relative));
        using var byKey = await client.GetAsync(
            new Uri($"/accounts/{Account}/audit?apiKeyId={keyId}&pageSize=100", UriKind.Relative));
        using var byOtherKey = await client.GetAsync(
            new Uri($"/accounts/{Account}/audit?apiKeyId={Guid.NewGuid()}&pageSize=100", UriKind.Relative));

        Count(await byPrefix.Content.ReadAsStringAsync()).Should().BeGreaterThan(0);
        Count(await byKey.Content.ReadAsStringAsync()).Should().BeGreaterThan(0);
        Count(await byOtherKey.Content.ReadAsStringAsync()).Should().Be(0, "no request used that key");
    }

    private static int Count(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("events").GetArrayLength();
    }
}
