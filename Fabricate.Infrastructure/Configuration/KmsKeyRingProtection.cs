using System.Security.Cryptography;
using System.Xml.Linq;
using Amazon.KeyManagementService;
using Amazon.KeyManagementService.Model;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.Extensions.DependencyInjection;

namespace Fabricate.Infrastructure.Configuration;

/// <summary>
/// Wraps the Data Protection key ring with a key-encryption key held in AWS KMS (#76).
///
/// <para>
/// This is what makes the database key store safe. Without it the ring sits in the same database as the
/// ciphertext it protects, so one dump yields both halves. With it the database holds only wrapped keys, and
/// unwrapping needs a KMS permission that a database dump does not carry — the two secrets are separated again,
/// which is the property the file-system store had and the database store otherwise gives up.
/// </para>
///
/// <para>
/// Envelope encryption rather than encrypting the XML with KMS directly: KMS caps <c>Encrypt</c> at 4 KB, and a
/// key ring element is comfortably larger. A data key is generated per element, the element is encrypted with it
/// under AES-GCM, and only the KMS-wrapped copy of that data key is stored. The plaintext data key never reaches
/// the database and is not retained after the write.
/// </para>
/// </summary>
public sealed class KmsXmlEncryptor(IAmazonKeyManagementService kms, string keyId) : IXmlEncryptor
{
    internal const string ElementName = "encryptedKey";

    public EncryptedXmlInfo Encrypt(XElement plaintextElement)
    {
        ArgumentNullException.ThrowIfNull(plaintextElement);

        // GenerateDataKey returns the same key twice: in the clear for us to use now, and wrapped by the KEK for
        // storage. Only the second is written down.
        var dataKey = kms.GenerateDataKeyAsync(new GenerateDataKeyRequest
        {
            KeyId = keyId,
            KeySpec = DataKeySpec.AES_256,
        }).GetAwaiter().GetResult();

        var plaintextKey = dataKey.Plaintext.ToArray();
        try
        {
            var plaintext = System.Text.Encoding.UTF8.GetBytes(plaintextElement.ToString(SaveOptions.DisableFormatting));

            var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
            var cipherText = new byte[plaintext.Length];
            var tag = new byte[AesGcm.TagByteSizes.MaxSize];

            using (var aes = new AesGcm(plaintextKey, tag.Length))
            {
                aes.Encrypt(nonce, plaintext, cipherText, tag);
            }

            var element = new XElement(ElementName,
                new XElement("kmsKeyId", keyId),
                new XElement("wrappedKey", Convert.ToBase64String(dataKey.CiphertextBlob.ToArray())),
                new XElement("nonce", Convert.ToBase64String(nonce)),
                new XElement("tag", Convert.ToBase64String(tag)),
                new XElement("value", Convert.ToBase64String(cipherText)));

            return new EncryptedXmlInfo(element, typeof(KmsXmlDecryptor));
        }
        finally
        {
            // The plaintext data key has done its work; do not leave it in a heap buffer for a dump to find.
            CryptographicOperations.ZeroMemory(plaintextKey);
        }
    }
}

/// <summary>
/// Unwraps what <see cref="KmsXmlEncryptor"/> wrote.
/// </summary>
/// <remarks>
/// The constructor signature is dictated by Data Protection, not chosen: it records the decryptor's type name in
/// the stored element and later instantiates it through its own activator, which understands a parameterless
/// constructor or one taking <see cref="IServiceProvider"/> and nothing else. Injecting the KMS client directly
/// compiles, registers cleanly, and then fails only when a wrapped key is read back — with
/// <c>MissingMethodException: no parameterless constructor defined</c> wrapped inside a
/// <c>CryptographicException</c>, several layers from the cause.
/// </remarks>
public sealed class KmsXmlDecryptor(IServiceProvider services) : IXmlDecryptor
{
    private IAmazonKeyManagementService kms => services.GetRequiredService<IAmazonKeyManagementService>();

    public XElement Decrypt(XElement encryptedElement)
    {
        ArgumentNullException.ThrowIfNull(encryptedElement);

        var wrappedKey = Read(encryptedElement, "wrappedKey");
        var nonce = Read(encryptedElement, "nonce");
        var tag = Read(encryptedElement, "tag");
        var cipherText = Read(encryptedElement, "value");

        // The key id travels with the ciphertext so a rotated KEK still decrypts what the previous one wrapped:
        // KMS resolves the key from the blob itself, and this is here for diagnosis rather than for the call.
        var decrypted = kms.DecryptAsync(new DecryptRequest
        {
            CiphertextBlob = new MemoryStream(wrappedKey),
            KeyId = encryptedElement.Element("kmsKeyId")?.Value,
        }).GetAwaiter().GetResult();

        var plaintextKey = decrypted.Plaintext.ToArray();
        try
        {
            var plaintext = new byte[cipherText.Length];
            using var aes = new AesGcm(plaintextKey, tag.Length);
            aes.Decrypt(nonce, cipherText, tag, plaintext);

            return XElement.Parse(System.Text.Encoding.UTF8.GetString(plaintext));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintextKey);
        }
    }

    private static byte[] Read(XElement element, string name)
    {
        var value = element.Element(name)?.Value
            ?? throw new InvalidOperationException(
                $"The wrapped key ring element is missing <{name}>. It was not written by this application.");

        return Convert.FromBase64String(value);
    }
}
