using System.Security.Cryptography;
using Fabricate.Domain.Models;
using Fabricate.Infrastructure.Export;
using FluentAssertions;

namespace Fabricate.Tests.Integration;

/// <summary>
/// #80: "the same seed always produces the same dataset" is the product's headline guarantee, and it was only
/// covered at the IRandomService level. These run the whole pipeline twice and compare the exported bytes, so a
/// DateTime.UtcNow in a generator, an unordered dictionary, or future parallel table generation breaks the build.
/// </summary>
public sealed class DeterminismTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"fabricate-determinism-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private async Task<string> GenerateAndExportAsync(long seed, string label)
    {
        var target = Path.Combine(_root, label);
        Directory.CreateDirectory(target);

        var orchestrator = GenerationFixture.CreateOrchestrator(seed);
        var (result, _) = await orchestrator.GenerateAsync(GenerationFixture.CreateRequest(seed));

        await new CsvExporter().ExportAsync(result.Tables, Path.Combine(target, "csv"));
        await new JsonExporter().ExportAsync(result.Tables, Path.Combine(target, "json"));
        await new SqlExporter().ExportAsync(result.Tables, Path.Combine(target, "sql"));
        await new ParquetExporter().ExportAsync(result.Tables, Path.Combine(target, "parquet"));

        return target;
    }

    private static Dictionary<string, string> HashTree(string root)
    {
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            hashes[relative] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file)));
        }
        return hashes;
    }

    [Fact]
    public async Task SameSeedAndInputs_ProduceByteIdenticalArtifactsAcrossEveryExporter()
    {
        var first = await GenerateAndExportAsync(4242, "run-a");
        var second = await GenerateAndExportAsync(4242, "run-b");

        var a = HashTree(first);
        var b = HashTree(second);

        a.Keys.Should().BeEquivalentTo(b.Keys, "both runs must write the same set of artifacts");
        a.Should().HaveCountGreaterThan(8, "csv, json, sql and parquet for three tables");
        foreach (var (file, hash) in a)
        {
            b[file].Should().Be(hash, $"{file} must be byte-identical between two runs with the same seed");
        }
    }

    [Fact]
    public async Task ADifferentSeed_ChangesValuesButPreservesEveryStructuralInvariant()
    {
        var (baseline, _) = await GenerationFixture.CreateOrchestrator(1).GenerateAsync(GenerationFixture.CreateRequest(1));
        var (variant, _) = await GenerationFixture.CreateOrchestrator(2).GenerateAsync(GenerationFixture.CreateRequest(2));

        // Row counts hold.
        foreach (var table in GenerationFixture.TablesInDependencyOrder)
        {
            Rows(variant, table).Count.Should().Be(Rows(baseline, table).Count);
        }

        // Values differ.
        var baselineEmails = Rows(baseline, "main.users").Select(r => r["email"]?.ToString()).ToArray();
        var variantEmails = Rows(variant, "main.users").Select(r => r["email"]?.ToString()).ToArray();
        variantEmails.Should().NotEqual(baselineEmails);

        // Uniqueness, nullability and referential integrity hold for both.
        foreach (var dataset in new[] { baseline, variant })
        {
            var userIds = Rows(dataset, "main.users").Select(r => Convert.ToInt64(r["id"])).ToArray();
            userIds.Should().OnlyHaveUniqueItems();
            Rows(dataset, "main.users").Select(r => r["email"]!.ToString()).Should().OnlyHaveUniqueItems();
            Rows(dataset, "main.users").Should().OnlyContain(r => r["display_name"] != null);

            var orderIds = Rows(dataset, "main.orders").Select(r => Convert.ToInt64(r["id"])).ToArray();
            Rows(dataset, "main.orders").Select(r => Convert.ToInt64(r["user_id"])).Should().BeSubsetOf(userIds);
            Rows(dataset, "main.order_items").Select(r => Convert.ToInt64(r["order_id"])).Should().BeSubsetOf(orderIds);

            // The self-reference must point at a real row when it is set.
            foreach (var managerId in Rows(dataset, "main.users").Select(r => r["manager_id"]).Where(v => v is not null))
            {
                userIds.Should().Contain(Convert.ToInt64(managerId));
            }
        }
    }

    [Fact]
    public async Task GenerationIsIndependentOfWallClockAndTableIterationOrder()
    {
        // Two runs separated in time, with the request dictionary built in a different order, must still match.
        var forward = GenerationFixture.CreateRequest(7);
        var (first, _) = await GenerationFixture.CreateOrchestrator(7).GenerateAsync(forward);

        await Task.Delay(25);

        var reordered = new GenerationRequest(
            GenerationFixture.Schema,
            new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["main.order_items"] = forward.RequestedRowCounts["main.order_items"],
                ["main.users"] = forward.RequestedRowCounts["main.users"],
                ["main.orders"] = forward.RequestedRowCounts["main.orders"],
            },
            7);
        var (second, _) = await GenerationFixture.CreateOrchestrator(7).GenerateAsync(reordered);

        foreach (var table in GenerationFixture.TablesInDependencyOrder)
        {
            Serialise(Rows(second, table)).Should().Be(Serialise(Rows(first, table)),
                $"{table} must not depend on the clock or on the order the row counts were supplied in");
        }
    }

    [Fact]
    public async Task TheRunSummaryCarriesTheSeedAndIsStableAcrossRuns()
    {
        var (_, firstSummary) = await GenerationFixture.CreateOrchestrator(11).GenerateAsync(GenerationFixture.CreateRequest(11));
        var (_, secondSummary) = await GenerationFixture.CreateOrchestrator(11).GenerateAsync(GenerationFixture.CreateRequest(11));

        firstSummary.Seed.Should().Be(11);
        secondSummary.Seed.Should().Be(firstSummary.Seed);
        secondSummary.TableCount.Should().Be(firstSummary.TableCount);
        secondSummary.RowCount.Should().Be(firstSummary.RowCount);
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows(GenerationResult result, string table)
        => result.Tables.Single(t => t.Table == table).Rows;

    private static string Serialise(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
        => string.Join("\n", rows.Select(r => string.Join("|", r.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"))));
}
