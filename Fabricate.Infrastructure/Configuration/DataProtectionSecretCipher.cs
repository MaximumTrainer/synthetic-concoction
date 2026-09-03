using Fabricate.Application.Abstractions;
using Microsoft.AspNetCore.DataProtection;

namespace Fabricate.Infrastructure.Configuration;

/// <summary>
/// <see cref="ISecretCipher"/> over ASP.NET Core Data Protection. The key ring lives outside the application database
/// (see <c>AddFabricateLlm</c>), and the purpose string is versioned so a future cipher change can coexist with rows
/// written under this one: <see cref="KeyVersion"/> is what selects the protector on decrypt.
/// </summary>
public sealed class DataProtectionSecretCipher(IDataProtectionProvider provider) : ISecretCipher
{
    public const string KeyVersion = "dp-v1";
    private const string PurposePrefix = "Fabricate.LlmCredentials.";

    public (string CipherText, string KeyVersion) Encrypt(string plaintext)
        => (Protector(KeyVersion).Protect(plaintext), KeyVersion);

    public string Decrypt(string cipherText, string keyVersion)
    {
        if (keyVersion != KeyVersion)
            throw new InvalidOperationException($"Unsupported secret key version '{keyVersion}'.");

        return Protector(keyVersion).Unprotect(cipherText);
    }

    private IDataProtector Protector(string keyVersion) => provider.CreateProtector(PurposePrefix + keyVersion);
}
