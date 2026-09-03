using Fabricate.Application.Generation;
using FluentAssertions;

namespace Fabricate.Tests.Application;

public sealed class DeterministicRandomServiceTests
{
    [Fact]
    public void SameSeedAndScope_ShouldProduceEquivalentSequences()
    {
        var first = new DeterministicRandomService(42);
        var second = new DeterministicRandomService(42);

        var sequenceA = Enumerable.Range(0, 10).Select(_ => first.NextInt("scope-a", 1, 1000)).ToArray();
        var sequenceB = Enumerable.Range(0, 10).Select(_ => second.NextInt("scope-a", 1, 1000)).ToArray();

        sequenceA.Should().Equal(sequenceB);
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
