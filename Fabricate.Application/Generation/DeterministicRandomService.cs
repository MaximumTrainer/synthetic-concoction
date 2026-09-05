using System.Security.Cryptography;
using System.Text;
using Fabricate.Application.Abstractions;

namespace Fabricate.Application.Generation;

/// <summary>
/// Derives values from (seed, scope). A scope names a <em>value</em>, not a stream: drawing the same scope twice
/// yields the same result, so generation does not depend on evaluation order or on how many times a scope was
/// touched before. Callers that need several values derive several scopes (<c>scope + ".tld"</c>, and so on).
/// </summary>
/// <remarks>
/// This previously memoised one <see cref="Random"/> per scope in a ConcurrentDictionary. Because generation
/// scopes embed the row index, that cache grew by one entry per column per row and was never released — roughly
/// 3.2 KB of live heap per generated row — which made the streaming export path scale linearly with row count
/// rather than staying bounded (#82). Every scope is drawn exactly once, so the cache never served a hit and
/// deriving on demand produces identical values.
/// </remarks>
public sealed class DeterministicRandomService(long seed) : IRandomService
{
    public int NextInt(string scope, int minInclusive, int maxExclusive)
        => ForScope(scope).Next(minInclusive, maxExclusive);

    public long NextLong(string scope, long minInclusive, long maxExclusive)
    {
        if (minInclusive >= maxExclusive)
        {
            throw new ArgumentOutOfRangeException(nameof(maxExclusive), "maxExclusive must be greater than minInclusive.");
        }

        var bytes = new byte[8];
        ForScope(scope).NextBytes(bytes);
        var value = BitConverter.ToInt64(bytes, 0) & long.MaxValue;
        return minInclusive + (value % (maxExclusive - minInclusive));
    }

    public double NextDouble(string scope)
        => ForScope(scope).NextDouble();

    public string NextToken(string scope, int length)
    {
        const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var random = ForScope(scope);
        var buffer = new char[length];

        for (var i = 0; i < buffer.Length; i++)
        {
            buffer[i] = chars[random.Next(0, chars.Length)];
        }

        return new string(buffer);
    }

    public Guid NextGuid(string scope)
    {
        Span<byte> bytes = stackalloc byte[16];
        ForScope(scope).NextBytes(bytes);
        return new Guid(bytes);
    }

    private Random ForScope(string scope)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes($"{seed}:{scope}"), hash);
        return new Random(BitConverter.ToInt32(hash[..4]));
    }
}
