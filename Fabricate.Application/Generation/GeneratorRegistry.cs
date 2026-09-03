using System.Collections.Concurrent;
using Fabricate.Application.Abstractions;
using Fabricate.Domain.Enums;

namespace Fabricate.Application.Generation;

public sealed class GeneratorRegistry : IGeneratorRegistry, IValueGeneratorDispatcher
{
    private readonly ConcurrentDictionary<(DataKind Kind, string Strategy), Func<GeneratorContext, CancellationToken, ValueTask<object?>>> _generators = new();

    public void Register(DataKind kind, Func<GeneratorContext, CancellationToken, ValueTask<object?>> generator, string? strategy = null)
    {
        var key = (kind, NormalizeStrategy(strategy));
        _generators[key] = generator;
    }

    public bool TryResolve(DataKind kind, string? strategy, out Func<GeneratorContext, CancellationToken, ValueTask<object?>> generator)
    {
        if (_generators.TryGetValue((kind, NormalizeStrategy(strategy)), out generator!))
        {
            return true;
        }

        return _generators.TryGetValue((kind, string.Empty), out generator!);
    }

    public ValueTask<object?> GenerateAsync(GeneratorContext context, CancellationToken cancellationToken = default)
    {
        var strategy = ResolveStrategy(context);
        if (!TryResolve(context.DataKind, strategy, out var generator))
        {
            throw new InvalidOperationException($"No generator is registered for DataKind '{context.DataKind}' and strategy '{strategy ?? "<default>"}'.");
        }

        return generator(context, cancellationToken);
    }

    private static string NormalizeStrategy(string? strategy) => strategy?.Trim().ToLowerInvariant() ?? string.Empty;

    private static string? ResolveStrategy(GeneratorContext context)
    {
        var tableRule = context.Rules?.Tables.FirstOrDefault(t => string.Equals(t.Table, context.Table, StringComparison.OrdinalIgnoreCase));
        var columnRule = tableRule?.Columns.FirstOrDefault(c => string.Equals(c.Column, context.Column, StringComparison.OrdinalIgnoreCase));
        return columnRule?.Strategy;
    }
}
