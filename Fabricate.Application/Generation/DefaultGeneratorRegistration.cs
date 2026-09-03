using Fabricate.Application.Abstractions;
using Fabricate.Domain.Enums;

namespace Fabricate.Application.Generation;

public static class DefaultGeneratorRegistration
{
    // First-name and last-name word lists for realistic deterministic name generation.
    private static readonly string[] FirstNames =
    [
        "Alice", "Bob", "Carol", "David", "Eva", "Frank", "Grace", "Henry", "Isla", "Jack",
        "Karen", "Leo", "Mia", "Noel", "Olivia", "Paul", "Quinn", "Ruth", "Sam", "Tara",
        "Uma", "Victor", "Wendy", "Xander", "Yara", "Zane", "Amy", "Ben", "Chloe", "Dan"
    ];

    private static readonly string[] LastNames =
    [
        "Smith", "Jones", "Taylor", "Brown", "Davies", "Evans", "Wilson", "Thomas", "Roberts", "Johnson",
        "Lewis", "Walker", "Robinson", "White", "Thompson", "Hughes", "Martin", "Jackson", "Clarke", "Hall",
        "Wood", "Harris", "Young", "King", "Moore", "Scott", "Baker", "Green", "Adams", "Hill"
    ];

    private static readonly string[] Streets =
    [
        "Main St", "High St", "Oak Ave", "Elm St", "Park Rd", "Church Ln", "Mill Rd", "Station Rd",
        "Victoria Rd", "Green Lane", "West St", "North Rd", "South St", "East Ave", "Bridge Rd"
    ];

    private static readonly string[] Countries =
    [
        "US", "GB", "CA", "AU", "DE", "FR", "NL", "JP", "SG", "IN",
        "BR", "MX", "ZA", "NG", "KR", "ES", "IT", "SE", "NO", "CH"
    ];

    private static readonly string[] TLDs = ["com", "org", "net", "io", "co.uk", "dev"];

    private static readonly string[] Companies =
    [
        "Acme Corp", "Globex", "Initech", "Umbrella Ltd", "Stark Industries", "Wayne Enterprises",
        "Dunder Mifflin", "Pied Piper", "Hooli", "Vandelay Industries", "Bluth Company", "Pendant Publishing",
        "Prestige Worldwide", "Spring Water", "Wernham Hogg", "Seinfeld Corp", "Cogsworth Inc", "NextGen Labs"
    ];

    private static readonly string[] Currencies = ["USD", "EUR", "GBP", "JPY", "CAD", "AUD", "CHF", "SGD", "HKD", "INR"];

    public static IGeneratorRegistry RegisterDefaults(this IGeneratorRegistry registry, IRandomService random)
    {
        registry.Register(DataKind.Boolean, (ctx, _) => new ValueTask<object?>(random.NextInt(Scope(ctx), 0, 2) == 1));
        registry.Register(DataKind.Integer, (ctx, _) =>
        {
            var rule = FindColumnRule(ctx);
            var min = rule?.MinValue is not null && int.TryParse(rule.MinValue, out var mn) ? mn : 1;
            var max = rule?.MaxValue is not null && int.TryParse(rule.MaxValue, out var mx) ? mx : 100_000;
            if (min >= max) max = min + 1;
            return new ValueTask<object?>(random.NextInt(Scope(ctx), min, max));
        });
        registry.Register(DataKind.Long, (ctx, _) =>
        {
            var rule = FindColumnRule(ctx);
            var min = rule?.MinValue is not null && long.TryParse(rule.MinValue, out var mn) ? mn : 1L;
            var max = rule?.MaxValue is not null && long.TryParse(rule.MaxValue, out var mx) ? mx : long.MaxValue;
            if (min >= max) max = min + 1;
            return new ValueTask<object?>(random.NextLong(Scope(ctx), min, max));
        });
        registry.Register(DataKind.Decimal, (ctx, _) =>
        {
            var rule = FindColumnRule(ctx);
            var min = rule?.MinValue is not null && double.TryParse(rule.MinValue, out var mn) ? mn : 0.0;
            var max = rule?.MaxValue is not null && double.TryParse(rule.MaxValue, out var mx) ? mx : 10_000.0;
            if (min >= max) max = min + 1;
            return new ValueTask<object?>((decimal)(min + random.NextDouble(Scope(ctx)) * (max - min)));
        });
        registry.Register(DataKind.Double, (ctx, _) =>
        {
            var rule = FindColumnRule(ctx);
            var min = rule?.MinValue is not null && double.TryParse(rule.MinValue, out var mn) ? mn : 0.0;
            var max = rule?.MaxValue is not null && double.TryParse(rule.MaxValue, out var mx) ? mx : 10_000.0;
            if (min >= max) max = min + 1;
            return new ValueTask<object?>(min + random.NextDouble(Scope(ctx)) * (max - min));
        });
        registry.Register(DataKind.String, (ctx, _) =>
        {
            var rule = FindColumnRule(ctx);
            if (!string.IsNullOrWhiteSpace(rule?.Pattern))
                return new ValueTask<object?>(GenerateFromPattern(rule.Pattern, random, Scope(ctx)));
            return new ValueTask<object?>($"{ctx.Column}_{random.NextToken(Scope(ctx), 10)}");
        });
        registry.Register(DataKind.Guid, (ctx, _) => new ValueTask<object?>(random.NextGuid(Scope(ctx))));
        registry.Register(DataKind.Date, (ctx, _) =>
        {
            var rule = FindColumnRule(ctx);
            var minDate = rule?.MinValue is not null && DateOnly.TryParse(rule.MinValue, out var mn) ? mn : DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-3650));
            var maxDate = rule?.MaxValue is not null && DateOnly.TryParse(rule.MaxValue, out var mx) ? mx : DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3650));
            var totalDays = (maxDate.DayNumber - minDate.DayNumber);
            if (totalDays <= 0) totalDays = 1;
            return new ValueTask<object?>(minDate.AddDays(random.NextInt(Scope(ctx), 0, totalDays + 1)));
        });
        registry.Register(DataKind.DateTime, (ctx, _) =>
        {
            var rule = FindColumnRule(ctx);
            var minDt = rule?.MinValue is not null && DateTimeOffset.TryParse(rule.MinValue, out var mn) ? mn : DateTimeOffset.UtcNow.AddMinutes(-1_000_000);
            var maxDt = rule?.MaxValue is not null && DateTimeOffset.TryParse(rule.MaxValue, out var mx) ? mx : DateTimeOffset.UtcNow.AddMinutes(1_000_000);
            var totalMinutes = (long)(maxDt - minDt).TotalMinutes;
            if (totalMinutes <= 0) totalMinutes = 1;
            return new ValueTask<object?>(minDt.AddMinutes(random.NextLong(Scope(ctx), 0, totalMinutes + 1)));
        });
        registry.Register(DataKind.TimestampTz, (ctx, _) =>
        {
            var rule = FindColumnRule(ctx);
            var minDt = rule?.MinValue is not null && DateTimeOffset.TryParse(rule.MinValue, out var mn) ? mn : DateTimeOffset.UtcNow.AddMinutes(-1_000_000);
            var maxDt = rule?.MaxValue is not null && DateTimeOffset.TryParse(rule.MaxValue, out var mx) ? mx : DateTimeOffset.UtcNow.AddMinutes(1_000_000);
            var totalMinutes = (long)(maxDt - minDt).TotalMinutes;
            if (totalMinutes <= 0) totalMinutes = 1;
            return new ValueTask<object?>(minDt.AddMinutes(random.NextLong(Scope(ctx), 0, totalMinutes + 1)));
        });
        registry.Register(DataKind.Json, (ctx, _) => new ValueTask<object?>($"{{\"id\":\"{random.NextToken(Scope(ctx), 8)}\"}}"));
        registry.Register(DataKind.Binary, (ctx, _) =>
        {
            var payload = random.NextToken(Scope(ctx), 16);
            return new ValueTask<object?>(System.Text.Encoding.UTF8.GetBytes(payload));
        });

        // Semantic kinds
        registry.Register(DataKind.Uuid, (ctx, _) => new ValueTask<object?>(random.NextGuid(Scope(ctx)).ToString()));
        registry.Register(DataKind.Email, (ctx, _) =>
        {
            var user = random.NextToken(Scope(ctx), 8).ToLowerInvariant();
            var tld = TLDs[random.NextInt(Scope(ctx) + ".tld", 0, TLDs.Length)];
            return new ValueTask<object?>($"{user}@example.{tld}");
        });
        registry.Register(DataKind.Phone, (ctx, _) =>
        {
            var num = random.NextLong(Scope(ctx), 2_000_000_000L, 9_999_999_999L);
            return new ValueTask<object?>($"+1{num}");
        });
        registry.Register(DataKind.FirstName, (ctx, _) => new ValueTask<object?>(FirstNames[random.NextInt(Scope(ctx), 0, FirstNames.Length)]));
        registry.Register(DataKind.LastName, (ctx, _) => new ValueTask<object?>(LastNames[random.NextInt(Scope(ctx), 0, LastNames.Length)]));
        registry.Register(DataKind.Name, (ctx, _) =>
        {
            var first = FirstNames[random.NextInt(Scope(ctx) + ".first", 0, FirstNames.Length)];
            var last = LastNames[random.NextInt(Scope(ctx) + ".last", 0, LastNames.Length)];
            return new ValueTask<object?>($"{first} {last}");
        });
        registry.Register(DataKind.Address, (ctx, _) =>
        {
            var number = random.NextInt(Scope(ctx) + ".num", 1, 999);
            var street = Streets[random.NextInt(Scope(ctx) + ".street", 0, Streets.Length)];
            return new ValueTask<object?>($"{number} {street}");
        });
        registry.Register(DataKind.PostalCode, (ctx, _) =>
        {
            var part1 = random.NextInt(Scope(ctx) + ".p1", 10000, 99999);
            return new ValueTask<object?>(part1.ToString());
        });
        registry.Register(DataKind.CountryCode, (ctx, _) => new ValueTask<object?>(Countries[random.NextInt(Scope(ctx), 0, Countries.Length)]));
        registry.Register(DataKind.Url, (ctx, _) =>
        {
            var slug = random.NextToken(Scope(ctx), 8).ToLowerInvariant();
            var tld = TLDs[random.NextInt(Scope(ctx) + ".tld", 0, TLDs.Length)];
            return new ValueTask<object?>($"https://{slug}.{tld}");
        });
        registry.Register(DataKind.IpAddress, (ctx, _) =>
        {
            var a = random.NextInt(Scope(ctx) + ".a", 1, 255);
            var b = random.NextInt(Scope(ctx) + ".b", 0, 256);
            var c = random.NextInt(Scope(ctx) + ".c", 0, 256);
            var d = random.NextInt(Scope(ctx) + ".d", 1, 255);
            return new ValueTask<object?>($"{a}.{b}.{c}.{d}");
        });
        registry.Register(DataKind.Currency, (ctx, _) => new ValueTask<object?>(Currencies[random.NextInt(Scope(ctx), 0, Currencies.Length)]));
        registry.Register(DataKind.CompanyName, (ctx, _) => new ValueTask<object?>(Companies[random.NextInt(Scope(ctx), 0, Companies.Length)]));
        registry.Register(DataKind.Text, (ctx, _) =>
        {
            const string lorem = "Lorem ipsum dolor sit amet consectetur adipiscing elit sed do eiusmod tempor incididunt ut labore";
            var words = lorem.Split(' ');
            var count = random.NextInt(Scope(ctx) + ".len", 5, 20);
            var offset = random.NextInt(Scope(ctx) + ".offset", 0, words.Length);
            var selected = Enumerable.Range(0, count).Select(i => words[(offset + i) % words.Length]);
            return new ValueTask<object?>(string.Join(" ", selected) + ".");
        });

        return registry;
    }

    private static string Scope(GeneratorContext context)
        => $"{context.Table}.{context.Column}.{context.RowIndex}";

    private static Fabricate.Domain.Models.ColumnRule? FindColumnRule(GeneratorContext ctx)
    {
        if (ctx.Rules is null) return null;
        foreach (var tableRule in ctx.Rules.Tables)
        {
            if (!string.Equals(tableRule.Table, ctx.Table, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var colRule in tableRule.Columns)
            {
                if (string.Equals(colRule.Column, ctx.Column, StringComparison.OrdinalIgnoreCase))
                    return colRule;
            }
        }
        return null;
    }

    /// <summary>
    /// Generates a string matching a simple character-class pattern like "[A-Z]{3}[0-9]{4}".
    /// Supported classes: [A-Z], [a-z], [0-9], [A-Za-z], [A-Za-z0-9].
    /// Literal characters outside brackets are emitted as-is.
    /// </summary>
    private static string GenerateFromPattern(string pattern, IRandomService random, string scope)
    {
        var result = new System.Text.StringBuilder();
        var i = 0;
        var charCount = 0;

        while (i < pattern.Length)
        {
            if (pattern[i] == '[')
            {
                var end = pattern.IndexOf(']', i + 1);
                if (end < 0) { result.Append(pattern[i++]); continue; }

                var charClass = pattern[(i + 1)..end];
                i = end + 1;

                // Parse optional quantifier {n}
                int repeat = 1;
                if (i < pattern.Length && pattern[i] == '{')
                {
                    var qEnd = pattern.IndexOf('}', i + 1);
                    if (qEnd > i + 1 && int.TryParse(pattern[(i + 1)..qEnd], out var n))
                    {
                        repeat = n;
                        i = qEnd + 1;
                    }
                }

                var chars = ExpandCharClass(charClass);
                for (var r = 0; r < repeat; r++)
                {
                    var ch = chars[random.NextInt($"{scope}.pat.{charCount++}", 0, chars.Length)];
                    result.Append(ch);
                }
            }
            else
            {
                result.Append(pattern[i++]);
            }
        }

        return result.ToString();
    }

    private static char[] ExpandCharClass(string charClass)
    {
        var chars = new List<char>();
        var i = 0;
        while (i < charClass.Length)
        {
            if (i + 2 < charClass.Length && charClass[i + 1] == '-')
            {
                var from = charClass[i];
                var to = charClass[i + 2];
                for (var c = from; c <= to; c++) chars.Add(c);
                i += 3;
            }
            else
            {
                chars.Add(charClass[i++]);
            }
        }
        return chars.Count > 0 ? chars.ToArray() : ['X'];
    }
}
