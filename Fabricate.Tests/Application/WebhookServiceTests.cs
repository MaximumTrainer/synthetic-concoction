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
    private readonly TestServices _services = new();
    private readonly WebhookService _service;
    private readonly Guid _adminId = Guid.NewGuid();
    private Guid _workspaceId;

    public WebhookServiceTests()
    {
        _service = new WebhookService(_repo, _services.WorkspaceService);
        // Webhooks are workspace-scoped and carry a signing secret, so every call is authorised (#79).
        _workspaceId = _services.WorkspaceService
            .CreateAsync(new CreateWorkspaceCommand(Guid.NewGuid(), "WS", _adminId)).GetAwaiter().GetResult().Id;
    }

    private async Task<Guid> CreateOtherWorkspaceAsync(Guid ownerId) =>
        (await _services.WorkspaceService.CreateAsync(new CreateWorkspaceCommand(Guid.NewGuid(), "Other", ownerId))).Id;

    [Fact]
    public async Task RegisterAsync_WithValidUrl_ShouldPersistWebhook()
    {
        var workspaceId = _workspaceId;
        var cmd = new RegisterWebhookCommand(workspaceId, "https://example.com/hook", ["run.completed"]);

        var result = await _service.RegisterAsync(cmd, _adminId);

        result.Id.Should().NotBeEmpty();
        result.Url.Should().Be("https://example.com/hook");
        result.Events.Should().ContainSingle("run.completed");
        result.IsActive.Should().BeTrue();
        result.WorkspaceId.Should().Be(workspaceId);
    }

    [Fact]
    public async Task RegisterAsync_WithInvalidUrl_ShouldThrow()
    {
        var cmd = new RegisterWebhookCommand(_workspaceId, "not-a-url", ["run.completed"]);

        var act = async () => await _service.RegisterAsync(cmd, _adminId);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*Webhook URL*");
    }

    [Fact]
    public async Task RegisterAsync_WithNoEvents_ShouldThrow()
    {
        var cmd = new RegisterWebhookCommand(_workspaceId, "https://example.com/hook", []);

        var act = async () => await _service.RegisterAsync(cmd, _adminId);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*event*");
    }

    [Fact]
    public async Task ListAsync_ShouldReturnWebhooksForWorkspace()
    {
        var workspaceId = _workspaceId;
        var otherWorkspaceId = await CreateOtherWorkspaceAsync(_adminId);

        await _service.RegisterAsync(new RegisterWebhookCommand(workspaceId, "https://a.com/h", ["run.completed"]), _adminId);
        await _service.RegisterAsync(new RegisterWebhookCommand(workspaceId, "https://b.com/h", ["run.failed"]), _adminId);
        await _service.RegisterAsync(new RegisterWebhookCommand(otherWorkspaceId, "https://c.com/h", ["run.completed"]), _adminId);

        var result = await _service.ListAsync(workspaceId, _adminId);

        result.Should().HaveCount(2);
        result.Should().AllSatisfy(w => w.WorkspaceId.Should().Be(workspaceId));
    }

    [Fact]
    public async Task DeleteAsync_WithExistingWebhook_ShouldRemoveIt()
    {
        var workspaceId = _workspaceId;
        var webhook = await _service.RegisterAsync(
            new RegisterWebhookCommand(workspaceId, "https://example.com/hook", ["run.completed"]),
            _adminId);

        await _service.DeleteAsync(webhook.Id, _adminId);

        var result = await _service.GetAsync(webhook.Id, _adminId);
        result.Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_WithNonExistentWebhook_ShouldThrow()
    {
        var act = async () => await _service.DeleteAsync(Guid.NewGuid(), _adminId);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not found*");
    }

    [Fact]
    public async Task AUserWithNoWorkspaceAccess_CannotRegisterListReadOrDelete()
    {
        var outsider = Guid.NewGuid();
        var webhook = await _service.RegisterAsync(
            new RegisterWebhookCommand(_workspaceId, "https://example.com/hook", ["run.completed"], "s3cret"), _adminId);

        var register = async () => await _service.RegisterAsync(
            new RegisterWebhookCommand(_workspaceId, "https://evil.example.com/h", ["run.completed"]), outsider);
        await register.Should().ThrowAsync<UnauthorizedAccessException>();

        var list = async () => await _service.ListAsync(_workspaceId, outsider);
        await list.Should().ThrowAsync<UnauthorizedAccessException>();

        // The signing secret must not be readable by an outsider; not-found rather than forbidden.
        (await _service.GetAsync(webhook.Id, outsider)).Should().BeNull();

        var delete = async () => await _service.DeleteAsync(webhook.Id, outsider);
        await delete.Should().ThrowAsync<InvalidOperationException>();
        (await _service.GetAsync(webhook.Id, _adminId)).Should().NotBeNull("the outsider's delete must not have taken effect");
    }

    [Fact]
    public async Task AViewerCanReadButNotRegisterOrDelete()
    {
        var viewer = Guid.NewGuid();
        await _services.WorkspaceService.GrantAccessAsync(
            new GrantWorkspaceAccessCommand(_workspaceId, viewer, false, WorkspaceRole.Viewer, _adminId));
        var webhook = await _service.RegisterAsync(
            new RegisterWebhookCommand(_workspaceId, "https://example.com/hook", ["run.completed"]), _adminId);

        (await _service.ListAsync(_workspaceId, viewer)).Should().ContainSingle();
        (await _service.GetAsync(webhook.Id, viewer)).Should().NotBeNull();

        var register = async () => await _service.RegisterAsync(
            new RegisterWebhookCommand(_workspaceId, "https://example.com/other", ["run.failed"]), viewer);
        await register.Should().ThrowAsync<UnauthorizedAccessException>();

        var delete = async () => await _service.DeleteAsync(webhook.Id, viewer);
        await delete.Should().ThrowAsync<UnauthorizedAccessException>();
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
