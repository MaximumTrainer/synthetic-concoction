using Fabricate.Infrastructure.Configuration;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;

namespace Fabricate.Tests.Infrastructure;

public sealed class DataProtectionSecretCipherTests
{
    [Fact]
    public void RoundTrip_RecoversPlaintext_AndCiphertextDoesNotContainIt()
    {
        var cipher = new DataProtectionSecretCipher(new EphemeralDataProtectionProvider());
        const string secret = "sk-ant-api03-round-trip-0001";

        var (cipherText, keyVersion) = cipher.Encrypt(secret);

        cipherText.Should().NotContain(secret);
        keyVersion.Should().Be(DataProtectionSecretCipher.KeyVersion);
        cipher.Decrypt(cipherText, keyVersion).Should().Be(secret);
    }

    [Fact]
    public void Encrypt_IsNonDeterministic()
    {
        var cipher = new DataProtectionSecretCipher(new EphemeralDataProtectionProvider());

        cipher.Encrypt("same").CipherText.Should().NotBe(cipher.Encrypt("same").CipherText);
    }

    [Fact]
    public void Decrypt_WithDifferentKeyRing_Fails()
    {
        var (cipherText, keyVersion) = new DataProtectionSecretCipher(new EphemeralDataProtectionProvider()).Encrypt("secret");
        var other = new DataProtectionSecretCipher(new EphemeralDataProtectionProvider());

        var act = () => other.Decrypt(cipherText, keyVersion);

        act.Should().Throw<System.Security.Cryptography.CryptographicException>();
    }

    [Fact]
    public void Decrypt_UnknownKeyVersion_IsRejected()
    {
        var cipher = new DataProtectionSecretCipher(new EphemeralDataProtectionProvider());
        var (cipherText, _) = cipher.Encrypt("secret");

        var act = () => cipher.Decrypt(cipherText, "dp-v99");

        act.Should().Throw<InvalidOperationException>().WithMessage("*dp-v99*");
    }
}
