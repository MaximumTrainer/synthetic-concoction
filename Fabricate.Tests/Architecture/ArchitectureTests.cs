using System.Reflection;
using FluentAssertions;

namespace Fabricate.Tests.Architecture;

/// <summary>
/// Hexagonal boundary guard (agents.md §1, #60): vendor SDKs and persistence frameworks may only be referenced from
/// Fabricate.Infrastructure. A reference creeping into Domain or Application fails the build here rather than in review.
/// </summary>
public sealed class ArchitectureTests
{
    private static readonly string[] VendorPrefixes =
    [
        "Anthropic", "AWSSDK", "Amazon", "Google", "Azure", "Microsoft.Azure", "OpenAI", "MongoDB",
        "Npgsql", "Microsoft.EntityFrameworkCore", "Microsoft.Data.Sqlite", "Microsoft.AspNetCore.DataProtection", "Parquet",
    ];

    public static TheoryData<string> InnerAssemblies => new()
    {
        typeof(Fabricate.Domain.Models.LlmProvider).Assembly.GetName().Name!,
        typeof(Fabricate.Application.Abstractions.IChatCompletionClient).Assembly.GetName().Name!,
    };

    [Theory]
    [MemberData(nameof(InnerAssemblies))]
    public void InnerLayers_ReferenceNoVendorSdkOrPersistenceFramework(string assemblyName)
    {
        var assembly = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == assemblyName);

        var offenders = assembly.GetReferencedAssemblies()
            .Select(r => r.Name!)
            .Where(name => VendorPrefixes.Any(p => name.Equals(p, StringComparison.Ordinal) || name.StartsWith(p + ".", StringComparison.Ordinal)))
            .ToArray();

        offenders.Should().BeEmpty($"{assemblyName} must depend only on abstractions; adapters live in Fabricate.Infrastructure");
    }

    [Fact]
    public void Domain_DoesNotReferenceApplicationOrInfrastructure()
    {
        var domain = typeof(Fabricate.Domain.Models.LlmProvider).Assembly;

        domain.GetReferencedAssemblies().Select(r => r.Name)
            .Should().NotContain(n => n!.StartsWith("Fabricate.", StringComparison.Ordinal), "dependencies point inward only");
    }

    [Fact]
    public void Application_DoesNotReferenceInfrastructureOrApi()
    {
        var application = typeof(Fabricate.Application.Abstractions.IChatCompletionClient).Assembly;

        application.GetReferencedAssemblies().Select(r => r.Name)
            .Should().NotContain(n => n == "Fabricate.Infrastructure" || n == "Fabricate.Api");
    }

    [Fact]
    public void VendorAdapters_LiveOnlyInInfrastructure()
    {
        var infrastructure = typeof(Fabricate.Infrastructure.Llm.ChatCompletionClientFactory).Assembly;

        var vendorRefs = infrastructure.GetReferencedAssemblies().Select(r => r.Name!)
            .Where(n => n.StartsWith("Anthropic", StringComparison.Ordinal) || n.StartsWith("Npgsql", StringComparison.Ordinal));

        vendorRefs.Should().NotBeEmpty("the adapters are expected here and nowhere else");
    }
}
