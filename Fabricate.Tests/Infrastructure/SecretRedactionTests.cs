using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Fabricate.Application.Abstractions;
using Fabricate.Application.Governance;
using Fabricate.Application.Llm;
using Fabricate.Application.Workspaces;
using Fabricate.Domain.Models;
using Fabricate.Infrastructure.DependencyInjection;
using Fabricate.Infrastructure.Llm;
using Fabricate.Infrastructure.Repositories;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Fabricate.Tests.Infrastructure;

/// <summary>
/// End-to-end redaction through the real composition root: a tenant secret goes in through the credential service,
/// out to a (canned) provider that echoes it back in an error body, and must appear in no log entry, no exception
/// message, no audit record, no API-shaped summary and no health projection.
/// </summary>
public sealed class SecretRedactionTests
{
    private const string Secret = "sk-ant-api03-REDACT-ME-8f7e6d5c4b3a";

    [Fact]
    public async Task Secret_NeverReachesLogs_Exceptions_Audit_OrSummaries()
    {
        var logs = new CapturingLoggerProvider();
        var audit = new InMemoryAuditLogRepository();

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(logs).SetMinimumLevel(LogLevel.Trace));
        services.AddSingleton<IAuditLogRepository>(audit);
        services.AddSingleton<IAccountRepository, InMemoryAccountRepository>();
        services.AddSingleton<IAuditLogService, AuditLogService>();
        services.AddSingleton<IWorkspaceRepository, InMemoryWorkspaceRepository>();
        services.AddSingleton<IAccountGroupRepository, InMemoryAccountGroupRepository>();
        services.AddSingleton<IWorkspaceService, WorkspaceService>();
        services.AddSingleton<ISecretProvider, EnvSecretProviderStub>();
        services.AddFabricateLlm(new LlmOptions { AllowPrivateEndpoints = true });

        // Route the OpenAI-compatible adapter to a provider that rejects the key and quotes it back.
        services.AddHttpClient(ChatCompletionClientFactory.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new EchoingFailureHandler(Secret));

        await using var provider = services.BuildServiceProvider();
        var workspaces = provider.GetRequiredService<IWorkspaceService>();
        var credentials = provider.GetRequiredService<ILlmCredentialService>();
        var resolver = provider.GetRequiredService<ILlmCredentialResolver>();
        var factory = provider.GetRequiredService<IChatCompletionClientFactory>();

        var adminId = Guid.NewGuid();
        var ws = await workspaces.CreateAsync(new CreateWorkspaceCommand(Guid.NewGuid(), "WS", adminId));

        var summary = await credentials.RegisterAsync(
            new RegisterLlmCredentialCommand(ws.Id, null, "k", LlmProvider.OpenAiCompatible, LlmCredentialKind.ApiKey, Secret, "gpt-x", "http://provider.local/v1"),
            adminId);
        var validation = await credentials.ValidateAsync(ws.Id, summary.Id, adminId);
        var rotated = await credentials.RotateAsync(ws.Id, summary.Id, Secret + "-v2", adminId);
        var listed = await credentials.ListAsync(ws.Id, adminId);

        var resolved = await resolver.ResolveAsync(ws.Id, null);
        var client = factory.Create(resolved!);
        Exception? providerFailure = null;
        try
        {
            await client.CompleteAsync(new ChatCompletionRequest("gpt-x", null, [LlmMessage.User("hi")], [], 16));
        }
        catch (LlmProviderException ex)
        {
            providerFailure = ex;
        }

        validation.IsValid.Should().BeFalse("the canned provider rejects every key");
        providerFailure.Should().NotBeNull();

        var surfaces = new Dictionary<string, string>
        {
            ["validation message"] = validation.Message,
            ["provider exception"] = providerFailure!.Message,
            ["provider exception (full)"] = providerFailure.ToString(),
            ["summary"] = System.Text.Json.JsonSerializer.Serialize(summary),
            ["rotated summary"] = System.Text.Json.JsonSerializer.Serialize(rotated),
            ["listing"] = System.Text.Json.JsonSerializer.Serialize(listed),
            ["resolved credential ToString"] = resolved!.ToString(),
            ["audit log"] = string.Join("\n", audit.All.Select(e => $"{e.Action} {e.Details}")),
            ["logs"] = string.Join("\n", logs.Entries),
        };

        foreach (var (surface, text) in surfaces)
        {
            text.Should().NotContain(Secret, $"the plaintext must not appear in the {surface}");
            text.Should().NotContain("REDACT-ME", $"no fragment of the plaintext may appear in the {surface}");
        }

        // The one legitimate consumer still gets it.
        resolved.GetSecret().Should().Be(Secret + "-v2");
    }

    private sealed class EnvSecretProviderStub : ISecretProvider
    {
        public Task<string> ResolveAsync(string secretName, CancellationToken ct = default) => throw new InvalidOperationException("not configured");
        public Task<bool> ExistsAsync(string secretName, CancellationToken ct = default) => Task.FromResult(false);
    }

    private sealed class EchoingFailureHandler(string secret) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("{\"error\":{\"message\":\"invalid api key " + secret + "\"}}", Encoding.UTF8, "application/json"),
            });
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentBag<string> Entries { get; } = [];
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, Entries);
        public void Dispose() { }

        private sealed class CapturingLogger(string category, ConcurrentBag<string> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => entries.Add($"[{category}] {formatter(state, exception)} {exception}");
        }
    }
}
