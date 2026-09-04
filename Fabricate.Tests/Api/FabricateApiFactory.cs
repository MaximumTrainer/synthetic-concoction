using Fabricate.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;

namespace Fabricate.Tests.Api;

/// <summary>
/// Boots the real API in-process (#79). Program.cs reads its configuration from process environment variables,
/// so the factory sets them before the host is built and restores them on dispose — which is why every fixture
/// using it belongs to the non-parallel <c>ApiIntegration</c> collection.
/// </summary>
public sealed class FabricateApiFactory : WebApplicationFactory<Program>
{
    public const string BootstrapApiKey = "test-bootstrap-key";

    private readonly Dictionary<string, string?> _originalEnvironment = [];
    private readonly Dictionary<string, string?> _environment;

    public FabricateApiFactory(IDictionary<string, string?>? environment = null)
    {
        _environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["FABRICATE__BootstrapApiKey"] = BootstrapApiKey,
            // In-memory repositories: these tests are about HTTP behaviour, not persistence.
            ["FABRICATE_DB_PROVIDER"] = "memory",
            ["FABRICATE_LLM_PROVIDER"] = null,
        };

        foreach (var (key, value) in environment ?? new Dictionary<string, string?>())
        {
            _environment[key] = value;
        }

        foreach (var (key, value) in _environment)
        {
            _originalEnvironment[key] = Environment.GetEnvironmentVariable(key);
            Environment.SetEnvironmentVariable(key, value);
        }
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Production);
        return base.CreateHost(builder);
    }

    /// <summary>A client authenticated with the seeded bootstrap key.</summary>
    public HttpClient CreateAuthenticatedClient(string? apiKey = null)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", apiKey ?? BootstrapApiKey);
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;

        foreach (var (key, value) in _originalEnvironment)
        {
            Environment.SetEnvironmentVariable(key, value);
        }
    }
}

/// <summary>API-level fixtures mutate process environment variables, so they must not run concurrently.</summary>
[CollectionDefinition("ApiIntegration", DisableParallelization = true)]
public sealed class ApiIntegrationCollection;
