using System.Net;
using System.Text;
using System.Security.Cryptography;
using Fabricate.Application.Abstractions;
using Fabricate.Application.Webhooks;
using Fabricate.Domain.Models;
using Fabricate.Infrastructure.Repositories;
using Fabricate.Infrastructure.Webhooks;
using FluentAssertions;

namespace Fabricate.Tests.Application;

public sealed class WebhookServiceTests
{
    private readonly InMemoryWebhookRepository _repo = new();
    private readonly WebhookService _service;

    public WebhookServiceTests()
    {
        _service = new WebhookService(_repo);
    }

    [Fact]
    public async Task RegisterAsync_WithValidUrl_ShouldPersistWebhook()
    {
        var workspaceId = Guid.NewGuid();
        var cmd = new RegisterWebhookCommand(workspaceId, "https://example.com/hook", ["run.completed"]);

        var result = await _service.RegisterAsync(cmd, Guid.NewGuid());

        result.Id.Should().NotBeEmpty();
        result.Url.Should().Be("https://example.com/hook");
        result.Events.Should().ContainSingle("run.completed");
        result.IsActive.Should().BeTrue();
        result.WorkspaceId.Should().Be(workspaceId);
    }

    [Fact]
    public async Task RegisterAsync_WithInvalidUrl_ShouldThrow()
    {
        var cmd = new RegisterWebhookCommand(Guid.NewGuid(), "not-a-url", ["run.completed"]);

        var act = async () => await _service.RegisterAsync(cmd, Guid.NewGuid());

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*Webhook URL*");
    }

    [Fact]
    public async Task RegisterAsync_WithNoEvents_ShouldThrow()
    {
        var cmd = new RegisterWebhookCommand(Guid.NewGuid(), "https://example.com/hook", []);

        var act = async () => await _service.RegisterAsync(cmd, Guid.NewGuid());

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*event*");
    }

    [Fact]
    public async Task ListAsync_ShouldReturnWebhooksForWorkspace()
    {
        var workspaceId = Guid.NewGuid();
        var otherWorkspaceId = Guid.NewGuid();

        await _service.RegisterAsync(new RegisterWebhookCommand(workspaceId, "https://a.com/h", ["run.completed"]), Guid.NewGuid());
        await _service.RegisterAsync(new RegisterWebhookCommand(workspaceId, "https://b.com/h", ["run.failed"]), Guid.NewGuid());
        await _service.RegisterAsync(new RegisterWebhookCommand(otherWorkspaceId, "https://c.com/h", ["run.completed"]), Guid.NewGuid());

        var result = await _service.ListAsync(workspaceId, Guid.NewGuid());

        result.Should().HaveCount(2);
        result.Should().AllSatisfy(w => w.WorkspaceId.Should().Be(workspaceId));
    }

    [Fact]
    public async Task DeleteAsync_WithExistingWebhook_ShouldRemoveIt()
    {
        var workspaceId = Guid.NewGuid();
        var webhook = await _service.RegisterAsync(
            new RegisterWebhookCommand(workspaceId, "https://example.com/hook", ["run.completed"]),
            Guid.NewGuid());

        await _service.DeleteAsync(webhook.Id, Guid.NewGuid());

        var result = await _service.GetAsync(webhook.Id, Guid.NewGuid());
        result.Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_WithNonExistentWebhook_ShouldThrow()
    {
        var act = async () => await _service.DeleteAsync(Guid.NewGuid(), Guid.NewGuid());
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not found*");
    }
}

public sealed class WebhookHmacTests
{
    [Fact]
    public void ComputeHmacSignature_IsConsistentForSameInputs()
    {
        var payload = """{"event":"run.completed"}""";
        var secret = "my-secret-123";

        var sig1 = HttpWebhookDeliveryService.ComputeHmacSignature(payload, secret);
        var sig2 = HttpWebhookDeliveryService.ComputeHmacSignature(payload, secret);

        sig1.Should().Be(sig2);
        sig1.Should().HaveLength(64); // SHA-256 = 32 bytes = 64 hex chars
    }

    [Fact]
    public void ComputeHmacSignature_DiffersForDifferentSecrets()
    {
        var payload = """{"event":"run.completed"}""";

        var sig1 = HttpWebhookDeliveryService.ComputeHmacSignature(payload, "secret-a");
        var sig2 = HttpWebhookDeliveryService.ComputeHmacSignature(payload, "secret-b");

        sig1.Should().NotBe(sig2);
    }

    [Fact]
    public void ComputeHmacSignature_MatchesStandardHmacSha256()
    {
        var payload = "test-payload";
        var secret = "key";

        var expected = Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();

        var result = HttpWebhookDeliveryService.ComputeHmacSignature(payload, secret);

        result.Should().Be(expected);
    }
}
