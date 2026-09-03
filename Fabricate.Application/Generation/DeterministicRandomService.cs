using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Fabricate.Application.Abstractions;

namespace Fabricate.Application.Generation;

public sealed class DeterministicRandomService(long seed) : IRandomService
{
    private readonly ConcurrentDictionary<string, Random> _scopedRandom = new(StringComparer.Ordinal);

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
        return _scopedRandom.GetOrAdd(scope, static (key, baseSeed) =>
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{baseSeed}:{key}"));
            var scopedSeed = BitConverter.ToInt32(hash.AsSpan()[..4]);
            return new Random(scopedSeed);
        }, seed);
    }
}
