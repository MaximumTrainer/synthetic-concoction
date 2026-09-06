using Fabricate.Application.Abstractions;
using Fabricate.Application.Llm;
using Fabricate.Infrastructure.Configuration;
using Fabricate.Infrastructure.DependencyInjection;
using Fabricate.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit.Abstractions;

namespace Fabricate.Tests.Integration;

/// <summary>
/// #76: tenant LLM credentials and connection secrets are encrypted with ASP.NET Core Data Protection, whose key
/// ring lived only on local disk. On Fly, Cloud Run, Container Apps or ECS that disk is ephemeral or unshared, so
/// two things went wrong and neither announced itself: a replaced machine took the ring with it and every stored
/// credential became permanently undecryptable, and two instances generated different rings and could not read
/// each other's rows.
///
/// <para>
/// These are the two failures, reproduced against a real PostgreSQL and then shown to be fixed by the database
/// key store. Both are about what survives the process, so each builds an entirely separate service provider —
/// a shared one would prove nothing.
/// </para>
/// </summary>
[Collection("SharedKeyRing")]
public sealed class SharedKeyRingTests(SharedKeyRingFixture fixture, ITestOutputHelper output)
    : IClassFixture<SharedKeyRingFixture>
{
    private const string Secret = "sk-ant-TENANT-CREDENTIAL-DO-NOT-LOSE";

    [Fact]
    public void ThePostgresContainerStartedWhenDockerIsAvailable()
    {
        output.WriteLine(fixture.Report());
        if (!fixture.DockerAvailable) return;

        fixture.ConnectionString.Should().NotBeNull(
            "PostgreSQL must start when Docker is available; it failed with: {0}", fixture.Failure);
    }

    // ── the failure the issue describes ──────────────────────────────────────────

    /// <summary>
    /// Two instances with their own key directories cannot read each other's ciphertext. This is the current
    /// default, and it is why a second instance breaks a deployment rather than scaling it.
    /// </summary>
    [Fact]
    public async Task TwoInstancesWithSeparateFileSystemRingsCannotReadEachOther()
    {
        if (fixture.ConnectionString is null) return;

        using var first = BuildInstance(FileSystemRing(NewKeyDirectory()));
        using var second = BuildInstance(FileSystemRing(NewKeyDirectory()));

        var (cipherText, keyVersion) = Cipher(first).Encrypt(Secret);

        var act = () => Cipher(second).Decrypt(cipherText, keyVersion);

        act.Should().Throw<Exception>(
            "a key ring on unshared disk is private to its instance, which is the defect #76 exists to fix");

        await Task.CompletedTask;
    }

    // ── the fix ──────────────────────────────────────────────────────────────────

    /// <summary>Acceptance criterion: two API instances against the same PostgreSQL decrypt each other's credentials.</summary>
    [Fact]
    public async Task TwoInstancesSharingOneDatabaseDecryptEachOthersCredentials()
    {
        if (fixture.ConnectionString is null) return;

        await using var schema = await MigrateAsync();

        using var first = BuildInstance(DatabaseRing());
        using var second = BuildInstance(DatabaseRing());

        var (cipherText, keyVersion) = Cipher(first).Encrypt(Secret);

        Cipher(second).Decrypt(cipherText, keyVersion).Should().Be(Secret,
            "both instances read one key ring out of the database they share");
    }

    /// <summary>
    /// Acceptance criterion: losing the container filesystem does not lose the ability to decrypt. The second
    /// instance here is what a replaced machine is — same database, nothing carried over from the first.
    /// </summary>
    [Fact]
    public async Task AReplacedInstanceStillDecryptsWhatItsPredecessorWrote()
    {
        if (fixture.ConnectionString is null) return;

        await using var schema = await MigrateAsync();

        string cipherText, keyVersion;
        using (var original = BuildInstance(DatabaseRing()))
        {
            (cipherText, keyVersion) = Cipher(original).Encrypt(Secret);
        }

        // The original process, its memory and its disk are gone. Only the database remains.
        using var replacement = BuildInstance(DatabaseRing());

        Cipher(replacement).Decrypt(cipherText, keyVersion).Should().Be(Secret);
    }

    /// <summary>The ring is genuinely in the database, not merely in a cache the two providers happen to share.</summary>
    [Fact]
    public async Task TheKeyRingIsWrittenToTheDatabase()
    {
        if (fixture.ConnectionString is null) return;

        await using var schema = await MigrateAsync();

        using var instance = BuildInstance(DatabaseRing());
        Cipher(instance).Encrypt(Secret);

        await using var context = NewContext();
        var keys = await context.DataProtectionKeys.ToListAsync();

        keys.Should().NotBeEmpty("Data Protection persists its ring through the application's DbContext");
        keys.Should().OnlyContain(k => k.Xml != null && k.Xml.Contains("<key", StringComparison.Ordinal));
    }

    // ── the refusal ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The database store puts the ring beside the ciphertext it protects, so one dump decrypts every tenant
    /// secret. That is a weaker position than the file-system store, not merely a different one, so it is refused
    /// at startup unless the operator says so — the same treatment every other footgun in the configuration gets.
    /// </summary>
    [Fact]
    public void TheDatabaseKeyRingIsRefusedWithoutAnExplicitAcknowledgement()
    {
        var options = new KeyRingOptions { KeyStore = "database" };

        options.Validate().Should().ContainSingle()
            .Which.Should().Contain("FABRICATE_DATA_PROTECTION_ALLOW_UNWRAPPED");

        var act = () => new ServiceCollection().AddFabricateLlm(new LlmOptions(), options);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Data Protection configuration is invalid*");
    }

    [Fact]
    public void AnUnknownKeyStoreIsRefused()
    {
        new KeyRingOptions { KeyStore = "s3" }.Validate()
            .Should().ContainSingle().Which.Should().Contain("must be one of");
    }

    [Fact]
    public void TheFileSystemStoreRemainsTheDefault()
    {
        var options = KeyRingOptions.FromEnvironment(_ => null);

        options.UsesDatabase.Should().BeFalse("an existing deployment must not change key store on upgrade");
        options.Validate().Should().BeEmpty();
    }

    // ── plumbing ─────────────────────────────────────────────────────────────────

    private KeyRingOptions FileSystemRing(string path) => new() { KeysPath = path };

    private static KeyRingOptions DatabaseRing() =>
        new() { KeyStore = "database", AllowUnwrappedDatabaseKeyRing = true };

    private static string NewKeyDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "fabricate-keyring-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>One instance: its own container, its own Data Protection registration, its own DbContext pool.</summary>
    private ServiceProvider BuildInstance(KeyRingOptions keyRing)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<FabricatePostgresDbContext>(o => o.UseNpgsql(fixture.ConnectionString!));
        services.AddScoped<FabricateDbContext>(sp => sp.GetRequiredService<FabricatePostgresDbContext>());
        services.AddFabricateLlm(new LlmOptions(), keyRing);
        return services.BuildServiceProvider();
    }

    private static ISecretCipher Cipher(ServiceProvider provider) => provider.GetRequiredService<ISecretCipher>();

    private FabricatePostgresDbContext NewContext()
        => new(new DbContextOptionsBuilder<FabricatePostgresDbContext>().UseNpgsql(fixture.ConnectionString!).Options);

    /// <summary>Applies migrations once per test, and hands back a handle that drops the ring afterwards.</summary>
    private async Task<KeyRingScope> MigrateAsync()
    {
        await using var context = NewContext();
        await context.Database.MigrateAsync();
        return new KeyRingScope(NewContext());
    }

    /// <summary>
    /// Clears the ring between tests. Data Protection caches its ring for the life of a provider, but the rows
    /// outlive the test, and a test that reused a previous test's key would pass for the wrong reason.
    /// </summary>
    private sealed class KeyRingScope(FabricatePostgresDbContext context) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await context.DataProtectionKeys.ExecuteDeleteAsync();
            await context.DisposeAsync();
        }
    }
}

/// <summary>One PostgreSQL for the suite. Every test builds its own service providers against it.</summary>
public sealed class SharedKeyRingFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _postgres;

    public string? ConnectionString { get; private set; }
    public string? Failure { get; private set; }
    public bool DockerAvailable { get; private set; }

    public string Report() =>
        ConnectionString is not null
            ? "Shared key ring (#76): EXERCISED against PostgreSQL."
            : Failure is not null
                ? $"Shared key ring (#76): FAILED — {Failure.Split('\n')[0].Trim()}"
                : "Shared key ring (#76): not run (no Docker).";

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("FABRICATE_SKIP_DOCKER_TESTS") == "1") return;

        try
        {
            _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
            await _postgres.StartAsync();
            ConnectionString = _postgres.GetConnectionString();
        }
        catch (Exception ex)
        {
            Failure = ex.ToString();
            ConnectionString = null;
            if (_postgres is not null) await _postgres.DisposeAsync();
            _postgres = null;
        }

        // Derived from the failure, not the success, so a broken container cannot silently disable the guard.
        DockerAvailable = ConnectionString is not null
            || (Failure is not null && !Failure.Contains("DockerUnavailableException", StringComparison.Ordinal));
    }

    public async Task DisposeAsync()
    {
        if (_postgres is not null) await _postgres.DisposeAsync();
    }
}

/// <summary>Migrations and the shared key-ring table make these tests unsafe to run beside each other.</summary>
[CollectionDefinition("SharedKeyRing", DisableParallelization = true)]
public sealed class SharedKeyRingCollection;
