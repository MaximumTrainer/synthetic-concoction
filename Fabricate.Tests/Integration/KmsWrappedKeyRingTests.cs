using Amazon.KeyManagementService;
using Amazon.KeyManagementService.Model;
using Fabricate.Application.Abstractions;
using Fabricate.Application.Llm;
using Fabricate.Infrastructure.Configuration;
using Fabricate.Infrastructure.DependencyInjection;
using Fabricate.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.LocalStack;
using Testcontainers.PostgreSql;
using Xunit.Abstractions;

namespace Fabricate.Tests.Integration;

/// <summary>
/// #76: the database key store puts the key ring in the same database as the ciphertext it protects, so one dump
/// yields both halves. A key-encryption key in KMS separates them again — the database then holds only wrapped
/// keys, and unwrapping needs a KMS permission a dump does not carry.
///
/// <para>
/// Verified against LocalStack rather than a real AWS account, so it runs in CI with no credentials. The property
/// under test is not "encryption happened" but "the plaintext is genuinely absent from the database and genuinely
/// unrecoverable without KMS", which is checked by reading the rows directly and by trying to start an instance
/// that has the database but not the key.
/// </para>
/// </summary>
[Collection("KmsKeyRing")]
public sealed class KmsWrappedKeyRingTests(KmsKeyRingFixture fixture, ITestOutputHelper output)
    : IClassFixture<KmsKeyRingFixture>
{
    private const string Secret = "sk-ant-WRAPPED-TENANT-CREDENTIAL";

    [Fact]
    public void BothContainersStartedWhenDockerIsAvailable()
    {
        output.WriteLine(fixture.Report());
        if (!fixture.DockerAvailable) return;

        fixture.PostgresConnectionString.Should().NotBeNull(
            "PostgreSQL must start; it failed with: {0}", fixture.Failure);
        fixture.KmsKeyId.Should().NotBeNull(
            "LocalStack KMS must start and hold a key; it failed with: {0}", fixture.Failure);
    }

    /// <summary>The ring still works: two instances sharing the database and the KEK read each other's rows.</summary>
    [Fact]
    public async Task TwoInstancesSharingOneWrappedRingDecryptEachOthersCredentials()
    {
        if (!fixture.Ready) return;
        await using var schema = await fixture.MigrateAsync();

        using var first = BuildInstance(WrappedRing());
        using var second = BuildInstance(WrappedRing());

        var (cipherText, keyVersion) = Cipher(first).Encrypt(Secret);

        Cipher(second).Decrypt(cipherText, keyVersion).Should().Be(Secret);
    }

    /// <summary>
    /// The point of the whole exercise: what lands in the database is wrapped. An unwrapped ring stores the key
    /// material as readable XML, so this asserts on the absence of the markers that would be there.
    /// </summary>
    [Fact]
    public async Task TheStoredKeyRingHoldsNoUsableKeyMaterial()
    {
        if (!fixture.Ready) return;
        await using var schema = await fixture.MigrateAsync();

        using var instance = BuildInstance(WrappedRing());
        Cipher(instance).Encrypt(Secret);

        await using var context = fixture.NewContext();
        var rows = await context.DataProtectionKeys.ToListAsync();

        var xml = rows.Should().ContainSingle().Subject.Xml!;

        xml.Should().Contain("encryptedKey", "the element is the wrapper written by KmsXmlEncryptor");
        xml.Should().Contain("wrappedKey", "the data key is stored only in its KMS-wrapped form");

        // Data Protection encrypts exactly one element — <masterKey>, the one marked requiresEncryption — and
        // leaves the rest of the descriptor in the clear on purpose: the algorithm names and the deserializer
        // type are what tell it how to read the key back, and they are not secret. So the assertion is about the
        // key material specifically, not about the XML looking opaque.
        xml.Should().NotContain("<masterKey",
            "the master key element is what an unwrapped ring stores in the clear, and it is the whole secret");
    }

    /// <summary>
    /// The separation, stated as a test: someone holding the database but no KMS access cannot read the ring.
    /// Without this the previous test only shows the stored XML looks different, not that it is protected.
    /// </summary>
    /// <remarks>
    /// The thief here is an instance configured for the database key store with no key-encryption key — which is
    /// what standing the application up against a stolen dump actually looks like.
    ///
    /// <para>
    /// The stronger-sounding test, pointing the thief at a <em>different</em> KMS key, does not work and is worth
    /// recording as a trap: KMS resolves the key from inside the ciphertext blob, so the <c>KeyId</c> on a decrypt
    /// request is a hint rather than a constraint. Real KMS raises <c>IncorrectKeyException</c> on a mismatch;
    /// LocalStack decrypts happily. A test written that way passes against AWS and silently proves nothing here.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AnInstanceWithTheDatabaseButNoKmsAccessCannotDecrypt()
    {
        if (!fixture.Ready) return;
        await using var schema = await fixture.MigrateAsync();

        string cipherText, keyVersion;
        using (var legitimate = BuildInstance(WrappedRing()))
        {
            (cipherText, keyVersion) = Cipher(legitimate).Encrypt(Secret);
        }

        using var thief = BuildInstance(new KeyRingOptions
        {
            KeyStore = "database",
            AllowUnwrappedDatabaseKeyRing = true,
        });

        var act = () => Cipher(thief).Decrypt(cipherText, keyVersion);

        act.Should().Throw<Exception>(
            "a stolen database is useless without the KMS access needed to unwrap its key ring");
    }

    /// <summary>A configured KEK lifts the acknowledgement, because it removes the thing being acknowledged.</summary>
    [Fact]
    public void AConfiguredKekRemovesTheNeedToAcknowledgeAnUnwrappedRing()
    {
        var wrapped = new KeyRingOptions
        {
            KeyStore = "database",
            Kek = "aws-kms",
            KmsKeyId = "alias/fabricate",
        };

        wrapped.Validate().Should().BeEmpty("a wrapped ring in the database is not the risk being guarded against");
    }

    [Fact]
    public void AwsKmsWithoutAKeyIdIsRefused()
    {
        new KeyRingOptions { KeyStore = "database", Kek = "aws-kms" }.Validate()
            .Should().ContainSingle().Which.Should().Contain("FABRICATE_DATA_PROTECTION_KMS_KEY_ID");
    }

    [Fact]
    public void AnUnknownKekIsRefused()
    {
        new KeyRingOptions { Kek = "hsm" }.Validate()
            .Should().Contain(e => e.Contains("FABRICATE_DATA_PROTECTION_KEK"));
    }

    // ── plumbing ─────────────────────────────────────────────────────────────────

    private KeyRingOptions WrappedRing(string? keyId = null) => new()
    {
        KeyStore = "database",
        Kek = "aws-kms",
        KmsKeyId = keyId ?? fixture.KmsKeyId,
        KmsRegion = "us-east-1",
        KmsServiceUrl = fixture.KmsServiceUrl,
    };

    private ServiceProvider BuildInstance(KeyRingOptions keyRing)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<FabricatePostgresDbContext>(o => o.UseNpgsql(fixture.PostgresConnectionString!));
        services.AddScoped<FabricateDbContext>(sp => sp.GetRequiredService<FabricatePostgresDbContext>());
        services.AddFabricateLlm(new LlmOptions(), keyRing);
        return services.BuildServiceProvider();
    }

    private static ISecretCipher Cipher(ServiceProvider provider) => provider.GetRequiredService<ISecretCipher>();
}

/// <summary>PostgreSQL for the ring, LocalStack for the KMS key that wraps it.</summary>
public sealed class KmsKeyRingFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _postgres;
    private LocalStackContainer? _localStack;

    public string? PostgresConnectionString { get; private set; }
    public string? KmsServiceUrl { get; private set; }
    public string? KmsKeyId { get; private set; }

    public string? Failure { get; private set; }
    public bool DockerAvailable { get; private set; }

    public bool Ready => PostgresConnectionString is not null && KmsKeyId is not null;

    public string Report() =>
        Ready
            ? "KMS-wrapped key ring (#76): EXERCISED against PostgreSQL and LocalStack KMS."
            : Failure is not null
                ? $"KMS-wrapped key ring (#76): FAILED — {Failure.Split('\n')[0].Trim()}"
                : "KMS-wrapped key ring (#76): not run (no Docker).";

    private string? _originalAccessKey;
    private string? _originalSecretKey;

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("FABRICATE_SKIP_DOCKER_TESTS") == "1") return;

        try
        {
            _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
            _localStack = new LocalStackBuilder("localstack/localstack:3.8").Build();

            await Task.WhenAll(_postgres.StartAsync(), _localStack.StartAsync());

            PostgresConnectionString = _postgres.GetConnectionString();
            KmsServiceUrl = _localStack.GetConnectionString();

            // LocalStack accepts any credentials, but the adapter resolves them from the standard chain rather
            // than taking keys, so the chain is given something to find.
            _originalAccessKey = Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID");
            _originalSecretKey = Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY");
            Environment.SetEnvironmentVariable("AWS_ACCESS_KEY_ID", "localstack");
            Environment.SetEnvironmentVariable("AWS_SECRET_ACCESS_KEY", "localstack");

            using var kms = new AmazonKeyManagementServiceClient(new AmazonKeyManagementServiceConfig
            {
                ServiceURL = KmsServiceUrl,
                AuthenticationRegion = "us-east-1",
            });

            KmsKeyId = (await kms.CreateKeyAsync(new CreateKeyRequest { Description = "fabricate key ring" })).KeyMetadata.KeyId;
        }
        catch (Exception ex)
        {
            Failure = ex.ToString();
            PostgresConnectionString = null;
            KmsKeyId = null;
        }

        DockerAvailable = Ready
            || (Failure is not null && !Failure.Contains("DockerUnavailableException", StringComparison.Ordinal));
    }

    public async Task DisposeAsync()
    {
        if (_postgres is not null) await _postgres.DisposeAsync();
        if (_localStack is not null) await _localStack.DisposeAsync();

        Environment.SetEnvironmentVariable("AWS_ACCESS_KEY_ID", _originalAccessKey);
        Environment.SetEnvironmentVariable("AWS_SECRET_ACCESS_KEY", _originalSecretKey);
    }

    public FabricatePostgresDbContext NewContext()
        => new(new DbContextOptionsBuilder<FabricatePostgresDbContext>().UseNpgsql(PostgresConnectionString!).Options);

    public async Task<KeyRingScope> MigrateAsync()
    {
        await using var context = NewContext();
        await context.Database.MigrateAsync();
        return new KeyRingScope(NewContext());
    }

    /// <summary>Clears the ring between tests so none of them passes on a key another one created.</summary>
    public sealed class KeyRingScope(FabricatePostgresDbContext context) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await context.DataProtectionKeys.ExecuteDeleteAsync();
            await context.DisposeAsync();
        }
    }
}

/// <summary>Shares the key-ring table and sets AWS credentials process-wide.</summary>
[CollectionDefinition("KmsKeyRing", DisableParallelization = true)]
public sealed class KmsKeyRingCollection;
