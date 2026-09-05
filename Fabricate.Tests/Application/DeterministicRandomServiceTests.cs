using Fabricate.Application.Generation;
using FluentAssertions;

namespace Fabricate.Tests.Application;

public sealed class DeterministicRandomServiceTests
{
    [Fact]
    public void SameSeedAndScope_ShouldProduceTheSameValueAcrossInstances()
    {
        var first = new DeterministicRandomService(42);
        var second = new DeterministicRandomService(42);

        first.NextInt("scope-a", 1, 1000).Should().Be(second.NextInt("scope-a", 1, 1000));
        first.NextToken("scope-b", 12).Should().Be(second.NextToken("scope-b", 12));
        first.NextGuid("scope-c").Should().Be(second.NextGuid("scope-c"));
        first.NextDouble("scope-d").Should().Be(second.NextDouble("scope-d"));
        first.NextLong("scope-e", 0, long.MaxValue).Should().Be(second.NextLong("scope-e", 0, long.MaxValue));
    }

    /// <summary>
    /// A scope names a value, not a stream. Drawing one repeatedly returns the same result, so generation cannot
    /// depend on evaluation order or on how many times a scope happened to be touched earlier in the run. This is
    /// also what lets the service stay stateless — the per-scope cache it used to keep grew once per column per
    /// row and never shrank, which is what put ~3.2 KB of live heap behind every streamed row (#82).
    /// </summary>
    [Fact]
    public void AScopeNamesAValue_SoRepeatedDrawsAreStable()
    {
        var random = new DeterministicRandomService(42);

        var draws = Enumerable.Range(0, 10).Select(_ => random.NextInt("same-scope", 1, 1_000_000)).ToArray();

        draws.Should().AllBeEquivalentTo(draws[0],
            "repeating a scope must repeat its value; callers needing several values derive several scopes");
    }

    [Fact]
    public void DistinctScopes_ShouldProduceDistinctValues()
    {
        var random = new DeterministicRandomService(42);

        var values = Enumerable.Range(0, 200)
            .Select(i => random.NextInt($"scope.{i}", 0, int.MaxValue))
            .ToArray();

        values.Distinct().Should().HaveCountGreaterThan(190,
            "suffixing a scope is how callers ask for another value, so distinct scopes must decorrelate");
    }

    [Fact]
    public void DifferentSeeds_ShouldProduceDifferentValues()
    {
        var first = new DeterministicRandomService(42);
        var second = new DeterministicRandomService(43);

        var one = first.NextToken("scope", 16);
        var two = second.NextToken("scope", 16);

        one.Should().NotBe(two);
    }
}
