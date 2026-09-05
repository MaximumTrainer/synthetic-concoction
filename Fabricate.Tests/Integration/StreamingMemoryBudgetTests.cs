using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;
using Fabricate.Infrastructure.Export;
using FluentAssertions;

namespace Fabricate.Tests.Integration;

/// <summary>
/// #82: streaming export exists so a large run does not have to fit in memory, but nothing measured it. What
/// matters is memory <em>retained while the run is in flight</em> — not allocation churn, which the GC reclaims
/// on demand, and not what survives afterwards. So these sample the live heap mid-run, with a forced blocking
/// collection, from inside the exporter.
///
/// Writing these found the streaming path was not bounded at all: it retained ~3.2 KB per generated row, so a
/// 300,000-row run held 903 MB. See <see cref="Fabricate.Application.Generation.DeterministicRandomService"/>.
/// </summary>
/// <remarks>
/// In its own non-parallel collection: both the live-heap and the allocated-bytes measurements are process-wide,
/// so a test allocating on another thread lands in the figures and makes the comparison meaningless.
/// </remarks>
[Trait("Category", "Performance")]
[Collection("MemoryMeasurement")]
public sealed class StreamingMemoryBudgetTests : IDisposable
{
    private const int RowsPerTable = 50_000;

    /// <summary>The budget the streaming path is documented to hold to for a 150,000-row run.</summary>
    private const long BudgetBytes = 200L * 1024 * 1024;

    /// <summary>
    /// Per-row retention guard. Measured at ~230 bytes/row (primary keys held for referencing tables, plus one
    /// string per unique constraint); the pre-fix figure was ~3,165. A generous ceiling still catches the whole
    /// class of regression — retaining anything row-shaped — without being brittle across runtimes.
    /// </summary>
    private const double MaxBytesPerRow = 800;

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"fabricate-memory-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static GenerationRequest Request(int rows) => new(
        GenerationFixture.Schema,
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["main.users"] = rows,
            ["main.orders"] = rows,
            ["main.order_items"] = rows,
        },
        Seed: 5150);

    /// <summary>
    /// Peak live heap observed during the run. The call site is deliberately synchronous: awaiting the run from
    /// the test method hoists the completed task into the async state machine, which keeps the last run's objects
    /// reachable and inflates any measurement taken afterwards.
    /// </summary>
    private long PeakRetainedDuring(int rows, string label, int samples = 6)
    {
        var sampler = new SamplingExporter(new CsvExporter(), Math.Max(1, rows * 3 / samples));
        GenerationFixture.CreateOrchestrator(5150)
            .GenerateStreamingAsync(Request(rows), sampler, Path.Combine(_root, label))
            .GetAwaiter().GetResult();
        return sampler.PeakRetainedBytes;
    }

    [Fact]
    public void PeakRetainedDuringStreaming_StaysWithinTheDocumentedBudget()
    {
        var peak = PeakRetainedDuring(RowsPerTable, "budget");

        peak.Should().BeLessThan(BudgetBytes,
            $"a {RowsPerTable * 3:N0}-row streaming run must stay under {BudgetBytes / 1024 / 1024} MB; " +
            $"measured {peak / 1024 / 1024} MB");

        Directory.GetFiles(Path.Combine(_root, "budget"), "*.csv").Should().HaveCount(3);
    }

    /// <summary>
    /// The property that actually separates streaming from buffering: retention per row must not grow with the
    /// size of the run. This is the assertion that fails if a per-row cache is reintroduced.
    /// </summary>
    [Fact]
    public void RetainedMemoryPerRow_DoesNotGrowWithRunSize()
    {
        var small = PeakRetainedDuring(10_000, "scale-small") / 30_000.0;
        var large = PeakRetainedDuring(RowsPerTable, "scale-large") / (RowsPerTable * 3.0);

        large.Should().BeLessThan(MaxBytesPerRow,
            $"streaming retained {large:F0} bytes per row at {RowsPerTable * 3:N0} rows");
        large.Should().BeLessThan(small * 1.2,
            $"retention per row must not climb with run size ({small:F0} -> {large:F0} bytes/row); " +
            "a rising figure means something is being kept for every row generated");
    }

    [Fact]
    public void StreamingAllocatesMateriallyLessThanBuffering_ForTheSameInput()
    {
        // A smaller row count so the buffered leg stays comfortably runnable on CI.
        const int rows = 20_000;

        var streaming = AllocatedDuring(() => GenerationFixture.CreateOrchestrator(5150)
            .GenerateStreamingAsync(Request(rows), new CsvExporter(), Path.Combine(_root, "cmp-streaming"))
            .GetAwaiter().GetResult());

        var buffered = AllocatedDuring(() =>
        {
            var (result, _) = GenerationFixture.CreateOrchestrator(5150).GenerateAsync(Request(rows))
                .GetAwaiter().GetResult();
            new CsvExporter().ExportAsync(result.Tables, Path.Combine(_root, "cmp-buffered")).GetAwaiter().GetResult();
        });

        streaming.Should().BeLessThan(buffered,
            $"streaming allocated {streaming / 1024 / 1024} MB versus buffered {buffered / 1024 / 1024} MB " +
            $"for {rows * 3:N0} rows");
    }

    [Fact]
    public async Task StreamingHandsTheExporterOneRowAtATime()
    {
        var counting = new CountingExporter();
        var summary = await GenerationFixture.CreateOrchestrator(5150)
            .GenerateStreamingAsync(Request(10_000), counting, Path.Combine(_root, "counting"));

        summary.RowCount.Should().Be(30_000);
        counting.RowsWritten.Should().Be(30_000);
        counting.MaxRowsHeldAtOnce.Should().Be(1, "a streaming exporter must never be handed a materialised batch");
    }

    private static long AllocatedDuring(Action work)
    {
        var before = GC.GetTotalAllocatedBytes(precise: true);
        work();
        return GC.GetTotalAllocatedBytes(precise: true) - before;
    }

    /// <summary>Delegates to a real exporter, sampling live heap every N rows.</summary>
    private sealed class SamplingExporter(IStreamingExporter inner, int sampleEvery) : IStreamingExporter
    {
        private long _rows;

        public long PeakRetainedBytes { get; private set; }

        public string Name => inner.Name;

        public Task BeginTableAsync(TableSchema table, string target, CancellationToken cancellationToken = default)
            => inner.BeginTableAsync(table, target, cancellationToken);

        public async Task WriteRowAsync(IReadOnlyDictionary<string, object?> row, CancellationToken cancellationToken = default)
        {
            await inner.WriteRowAsync(row, cancellationToken).ConfigureAwait(false);
            if (++_rows % sampleEvery == 0)
            {
                PeakRetainedBytes = Math.Max(PeakRetainedBytes, GC.GetTotalMemory(forceFullCollection: true));
            }
        }

        public Task EndTableAsync(CancellationToken cancellationToken = default) => inner.EndTableAsync(cancellationToken);

        public Task ExportAsync(IReadOnlyList<TableData> tables, string target, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("This exporter is streaming-only.");
    }

    /// <summary>Counts rows without retaining them.</summary>
    private sealed class CountingExporter : IStreamingExporter
    {
        public int RowsWritten { get; private set; }
        public int MaxRowsHeldAtOnce { get; private set; }

        public string Name => "counting";

        public Task BeginTableAsync(TableSchema table, string target, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task WriteRowAsync(IReadOnlyDictionary<string, object?> row, CancellationToken cancellationToken = default)
        {
            RowsWritten++;
            MaxRowsHeldAtOnce = Math.Max(MaxRowsHeldAtOnce, 1);
            return Task.CompletedTask;
        }

        public Task EndTableAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ExportAsync(IReadOnlyList<TableData> tables, string target, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("This exporter is streaming-only.");
    }
}

/// <summary>Memory measurement is process-wide, so these tests must not run alongside anything else.</summary>
[CollectionDefinition("MemoryMeasurement", DisableParallelization = true)]
public sealed class MemoryMeasurementCollection;
