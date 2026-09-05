using System.Net;
using System.Text.Json;
using Fabricate.Api;
using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Fabricate.Tests.Api;

/// <summary>
/// #74: the audit export over the real HTTP pipeline — authorisation, both formats, the window filter, and the
/// guarantee that nothing sensitive reaches the file that leaves the building.
/// </summary>
[Collection("ApiIntegration")]
public sealed class AuditExportApiTests
{
    private static readonly Guid Account = StartupBootstrapService.BootstrapAccountId;

    private static async Task SeedAsync(FabricateApiFactory factory, params AuditEvent[] events)
    {
        using var scope = factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IAuditLogRepository>();
        foreach (var auditEvent in events) await repository.AppendAsync(auditEvent);
    }

    private static AuditEvent Event(string action, DateTimeOffset occurredAt, string? details = null, Guid? accountId = null)
        => new(Guid.NewGuid(), accountId ?? Account, null, action, "Thing", "1", "corr", occurredAt, details);

    [Fact]
    public async Task ExportStreamsJsonForAnOwner()
    {
        using var factory = new FabricateApiFactory();
        var now = DateTimeOffset.UtcNow;
        await SeedAsync(factory,
            Event("first.event", now.AddMinutes(-10)),
            Event("second.event", now.AddMinutes(-5)));

        using var client = factory.CreateAuthenticatedClient();
        using var response = await client.GetAsync(new Uri($"/accounts/{Account}/audit/export?format=json", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        response.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var actions = document.RootElement.EnumerateArray().Select(e => e.GetProperty("action").GetString()).ToArray();
        actions.Should().Equal(["first.event", "second.event"], "the export is ordered oldest first");
    }

    [Fact]
    public async Task ExportStreamsCsvWithAHeaderAndQuotedFields()
    {
        using var factory = new FabricateApiFactory();
        await SeedAsync(factory, Event("comma.event", DateTimeOffset.UtcNow.AddMinutes(-1), "note=one,two"));

        using var client = factory.CreateAuthenticatedClient();
        using var response = await client.GetAsync(new Uri($"/accounts/{Account}/audit/export?format=csv", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");

        var lines = (await response.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines[0].Should().Be("id,accountId,actorUserId,action,targetType,targetId,correlationId,occurredAt,details");
        lines.Should().ContainSingle(l => l.Contains("\"comma.event\"", StringComparison.Ordinal));
        lines.Last().Should().Contain("\"note=one,two\"", "a field containing a comma must be quoted, not split");
    }

    [Fact]
    public async Task ExportRejectsAnUnknownFormat()
    {
        using var factory = new FabricateApiFactory();
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.GetAsync(new Uri($"/accounts/{Account}/audit/export?format=xlsx", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ExportRequiresAuthentication()
    {
        using var factory = new FabricateApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri($"/accounts/{Account}/audit/export", UriKind.Relative));

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ExportIsRefusedForAnAccountTheCallerDoesNotOwn()
    {
        using var factory = new FabricateApiFactory();
        var otherAccount = Guid.NewGuid();
        await SeedAsync(factory, Event("secret.event", DateTimeOffset.UtcNow, accountId: otherAccount));

        using var client = factory.CreateAuthenticatedClient();
        using var response = await client.GetAsync(new Uri($"/accounts/{otherAccount}/audit/export", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("secret.event",
            "a refusal must not leak the very rows it refused to export");
    }

    [Fact]
    public async Task ExportOmitsSecretsFingerprintsAndConnectionStrings()
    {
        using var factory = new FabricateApiFactory();
        var now = DateTimeOffset.UtcNow;
        await SeedAsync(factory,
            Event("llm.credential_registered", now.AddMinutes(-3),
                "provider=Anthropic;model=claude-opus-5;fingerprint=9f3c2a1b;secret=sk-ant-api03-LIVE-KEY-VALUE"),
            Event("connection.created", now.AddMinutes(-2),
                "target=Host=db.internal;Username=app;Password=hunter2;Database=prod"),
            Event("key.created", now.AddMinutes(-1), "apiKey=cnc_live_9182736455647382"));

        using var client = factory.CreateAuthenticatedClient();
        using var response = await client.GetAsync(new Uri($"/accounts/{Account}/audit/export?format=json", UriKind.Relative));
        var body = await response.Content.ReadAsStringAsync();

        body.Should().NotContain("sk-ant-api03-LIVE-KEY-VALUE");
        body.Should().NotContain("9f3c2a1b");
        body.Should().NotContain("hunter2");
        body.Should().NotContain("cnc_live_9182736455647382");

        body.Should().Contain("provider=Anthropic", "the export is only useful if the non-sensitive detail survives");
        body.Should().Contain("llm.credential_registered");
    }

    [Fact]
    public async Task ExportFiltersToTheRequestedWindow()
    {
        using var factory = new FabricateApiFactory();
        var now = DateTimeOffset.UtcNow;
        await SeedAsync(factory,
            Event("in.window", now.AddDays(-2)),
            Event("too.old", now.AddDays(-40)));

        var from = Uri.EscapeDataString(now.AddDays(-7).ToString("O"));
        var to = Uri.EscapeDataString(now.ToString("O"));

        using var client = factory.CreateAuthenticatedClient();
        using var response = await client.GetAsync(
            new Uri($"/accounts/{Account}/audit/export?from={from}&to={to}", UriKind.Relative));

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("in.window");
        body.Should().NotContain("too.old");
    }

    [Fact]
    public async Task TheQueryApiReturnsTheSameEventsTheExportDoes()
    {
        using var factory = new FabricateApiFactory();
        var now = DateTimeOffset.UtcNow;
        await SeedAsync(factory, Event("alpha", now.AddMinutes(-2)), Event("beta", now.AddMinutes(-1)));

        using var client = factory.CreateAuthenticatedClient();

        using var queried = await client.GetAsync(new Uri($"/accounts/{Account}/audit?pageSize=100", UriKind.Relative));
        queried.StatusCode.Should().Be(HttpStatusCode.OK);
        using var queryDocument = JsonDocument.Parse(await queried.Content.ReadAsStringAsync());
        var queryIds = queryDocument.RootElement.GetProperty("events")
            .EnumerateArray().Select(e => e.GetProperty("id").GetString()).OrderBy(id => id, StringComparer.Ordinal).ToArray();

        using var exported = await client.GetAsync(new Uri($"/accounts/{Account}/audit/export", UriKind.Relative));
        using var exportDocument = JsonDocument.Parse(await exported.Content.ReadAsStringAsync());
        var exportIds = exportDocument.RootElement
            .EnumerateArray().Select(e => e.GetProperty("id").GetString()).OrderBy(id => id, StringComparer.Ordinal).ToArray();

        exportIds.Should().Equal(queryIds);
        exportIds.Should().HaveCount(2);
    }
}
