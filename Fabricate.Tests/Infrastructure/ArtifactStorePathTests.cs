using System.Text;
using Fabricate.Application.Abstractions;
using Fabricate.Infrastructure.DependencyInjection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Fabricate.Tests.Infrastructure;

/// <summary>#61: the artifact directory is configurable so hosted deployments can point it at a mounted volume.</summary>
[Collection("EnvironmentVariables")]
public sealed class ArtifactStorePathTests
{
    private static IArtifactStore Resolve()
    {
        var services = new ServiceCollection();
        services.AddFabricateInfrastructure(o => { o.Provider = "sqlite"; o.ConnectionString = "Data Source=:memory:"; });
        return services.BuildServiceProvider().GetRequiredService<IArtifactStore>();
    }

    [Fact]
    public async Task ArtifactsPath_WhenConfigured_IsWhereArtifactsAreWritten()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"fabricate-artifacts-test-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable("FABRICATE_ARTIFACTS_PATH", dir);
        try
        {
            var store = Resolve();
            var path = await store.StoreAsync("run-1", "summary.json", new MemoryStream(Encoding.UTF8.GetBytes("{}")));

            Path.GetFullPath(path).Should().StartWith(Path.GetFullPath(dir));
            File.Exists(path).Should().BeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable("FABRICATE_ARTIFACTS_PATH", null);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ArtifactsPath_WhenUnset_FallsBackToTheTempDirectory()
    {
        Environment.SetEnvironmentVariable("FABRICATE_ARTIFACTS_PATH", null);

        var store = Resolve();
        var path = await store.StoreAsync("run-2", "summary.json", new MemoryStream(Encoding.UTF8.GetBytes("{}")));

        Path.GetFullPath(path).Should().StartWith(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "fabricate-artifacts")));
    }
}

/// <summary>Tests that mutate process environment variables must not run in parallel with each other.</summary>
[CollectionDefinition("EnvironmentVariables", DisableParallelization = true)]
public sealed class EnvironmentVariablesCollection;
