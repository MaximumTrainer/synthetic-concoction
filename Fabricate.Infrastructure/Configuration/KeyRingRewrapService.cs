using System.Xml.Linq;
using Fabricate.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Fabricate.Infrastructure.Configuration;

/// <summary>How a rewrap went, so an operator can tell an idle run from one that changed something.</summary>
public sealed record RewrapReport(int Total, int Rewrapped, int AlreadyCurrent)
{
    public override string ToString() =>
        $"{Total} key ring entries: {Rewrapped} rewrapped, {AlreadyCurrent} already protected by the current key.";
}

/// <summary>
/// Re-protects every entry in the Data Protection key ring under whatever protection is configured now (#76).
///
/// <para>
/// This exists because Data Protection encrypts on write and never revisits what it has already stored. An
/// operator who runs with an unwrapped ring, then configures a key-encryption key, gets protection on newly
/// created ring entries only — the existing ones stay in the clear indefinitely, while the configuration says
/// the ring is wrapped. That gap is silent, and it is the one an operator is least likely to check, because
/// enabling the KEK looks like it worked.
/// </para>
///
/// <para>
/// Rewrapping is done at the ring rather than on the rows: tenant ciphertext is unchanged, so nothing that
/// references a <c>KeyVersion</c> needs rewriting and there is no window in which a row is half-migrated. The
/// operation is idempotent — an entry already protected by the current encryptor is left alone.
/// </para>
/// </summary>
/// <remarks>
/// Rows are updated through the <see cref="FabricateDbContext"/> rather than through <c>IXmlRepository</c>.
/// That interface can only store and enumerate, and its EF implementation appends a row per call — so rewrapping
/// through it leaves the unwrapped entry sitting in the database next to its replacement, which is precisely the
/// plaintext the operation is supposed to remove. The interface cannot express "replace this entry", so this
/// deliberately steps around it.
/// </remarks>
public sealed class KeyRingRewrapService(IServiceProvider services)
{
    /// <summary>
    /// Data Protection marks an encrypted element with the decryptor that can read it. Finding that attribute is
    /// how an entry is recognised as wrapped, and by what.
    /// </summary>
    private const string DecryptorTypeAttribute = "decryptorType";

    /// <summary>
    /// Data Protection marks the element to encrypt with a <em>namespaced</em> attribute:
    /// <c>p4:requiresEncryption="true"</c> where <c>p4</c> is this namespace. Looking it up by the bare local
    /// name silently matches nothing, so a rewrap decrypts the entry, finds no element to protect, and writes
    /// the plaintext straight back — reporting success.
    /// </summary>
    private static readonly XName RequiresEncryption =
        XName.Get("requiresEncryption", "http://schemas.asp.net/2015/03/dataProtection");

    /// <summary>The wrapper element Data Protection puts an encrypted secret inside, matched to its own name.</summary>
    private static readonly XName EncryptedSecret =
        XName.Get("encryptedSecret", "http://schemas.asp.net/2015/03/dataProtection");

    public async Task<RewrapReport> RewrapAsync(CancellationToken cancellationToken = default)
    {
        var encryptor = services.GetRequiredService<IOptions<KeyManagementOptions>>().Value.XmlEncryptor;

        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetService<FabricateDbContext>()
            ?? throw new InvalidOperationException(
                "No database is configured, so there is no key ring to rewrap. This command applies to " +
                "FABRICATE_DATA_PROTECTION_KEY_STORE=database.");

        var rows = await context.DataProtectionKeys.ToListAsync(cancellationToken).ConfigureAwait(false);
        var rewrapped = 0;
        var current = 0;

        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(row.Xml)) continue;

            var element = XElement.Parse(row.Xml);

            if (IsAlreadyProtectedBy(element, encryptor))
            {
                current++;
                continue;
            }

            var decrypted = DecryptAll(element);
            var reprotected = encryptor is null ? decrypted : Protect(decrypted, encryptor);

            // In place: the unwrapped entry is overwritten, not kept beside its replacement.
            row.Xml = reprotected.ToString(SaveOptions.DisableFormatting);
            rewrapped++;
        }

        if (rewrapped > 0) await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new RewrapReport(rows.Count, rewrapped, current);
    }

    /// <summary>
    /// True when every encrypted part of the entry already names the decryptor the current encryptor would use.
    /// An entry with no encrypted part at all counts as current only when no encryptor is configured.
    /// </summary>
    private static bool IsAlreadyProtectedBy(XElement element, IXmlEncryptor? encryptor)
    {
        var encrypted = element.DescendantsAndSelf()
            .Where(e => e.Attribute(DecryptorTypeAttribute) is not null)
            .ToList();

        if (encryptor is null) return encrypted.Count == 0;
        if (encrypted.Count == 0) return false;

        // The encryptor names its decryptor on the way out; ask it rather than hard-coding the pairing, so this
        // keeps working when another KEK provider is added.
        var expected = encryptor.Encrypt(new XElement("probe")).DecryptorType;

        return encrypted.All(e =>
            e.Attribute(DecryptorTypeAttribute)!.Value.StartsWith(expected.FullName!, StringComparison.Ordinal));
    }

    /// <summary>Recursively replaces every encrypted element with its plaintext, using the decryptor it names.</summary>
    private XElement DecryptAll(XElement element)
    {
        var copy = new XElement(element);

        // ToList first: the query is over the tree being modified.
        foreach (var encrypted in copy.DescendantsAndSelf().Where(e => e.Attribute(DecryptorTypeAttribute) is not null).ToList())
        {
            var typeName = encrypted.Attribute(DecryptorTypeAttribute)!.Value;
            var type = Type.GetType(typeName)
                ?? throw new InvalidOperationException(
                    $"The key ring names decryptor '{typeName}', which this build cannot load. Rewrapping would " +
                    "destroy the entry, so it is refused.");

            var decryptor = (IXmlDecryptor)ActivatorUtilities.CreateInstance(services, type);

            // The element carrying decryptorType is a wrapper; what the decryptor wants is its single child.
            var payload = encrypted.Elements().SingleOrDefault()
                ?? throw new InvalidOperationException(
                    "A wrapped key ring entry must contain exactly one encrypted element. This one does not, so " +
                    "it was not written by a Data Protection encryptor and rewrapping it would destroy it.");

            var plaintext = decryptor.Decrypt(payload);

            if (ReferenceEquals(encrypted, copy)) return plaintext;
            encrypted.ReplaceWith(plaintext);
        }

        return copy;
    }

    /// <summary>
    /// Re-applies protection to the elements Data Protection marks as needing it, which is how it decides what to
    /// encrypt on a normal write.
    /// </summary>
    private static XElement Protect(XElement element, IXmlEncryptor encryptor)
    {
        var copy = new XElement(element);

        foreach (var secret in copy.DescendantsAndSelf()
                     .Where(e => (string?)e.Attribute(RequiresEncryption) == "true").ToList())
        {
            // Data Protection's own shape, which its reader depends on: a wrapper carrying decryptorType whose
            // single child is the encryptor's output. Putting the attribute on the encryptor's element instead
            // produces XML that looks right, stores cleanly, and fails on read with "sequence contains no
            // elements" — because the reader hands the decryptor `element.Elements().Single()`.
            var info = encryptor.Encrypt(secret);
            var wrapper = new XElement(EncryptedSecret, info.EncryptedElement);
            wrapper.SetAttributeValue(DecryptorTypeAttribute, info.DecryptorType.AssemblyQualifiedName);

            if (ReferenceEquals(secret, copy)) return wrapper;
            secret.ReplaceWith(wrapper);
        }

        return copy;
    }
}
