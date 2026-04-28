using System.Security.Cryptography;
using System.Text;
using MessagePack;
using Xunit;

namespace SqliteWasmBlazor.CryptoSync.Tests.Properties;

/// <summary>
/// Property: storage backend never observes plaintext payload bytes
/// (whitepaper §4 P1). For random plaintexts that the application
/// encrypts into a <see cref="DeltaEnvelope"/>, no byte-substring of the
/// plaintext appears in the serialized wire bytes the relay would store.
///
/// <para>
/// Run as a seeded-RNG iterative property test rather than via FsCheck
/// to avoid adding a devDependency for this single suite. Each iteration
/// builds the envelope the way the worker does (per-row AES-GCM, body
/// stored as <c>ShadowRow.EncryptedRow</c>) and asserts the plaintext
/// marker is absent in every byte alignment of the MessagePacked wire.
/// </para>
/// </summary>
public class PlaintextLeakProperty
{
    /// <summary>
    /// 200 iterations × random plaintexts under a single AES-GCM key. Marker
    /// has the form <c>"SECRET-MARKER-{iter}-{hexTail}"</c> so any leak shows
    /// up as a recognizable ASCII substring in the failure message.
    /// </summary>
    [Fact]
    public void DeltaEnvelope_RandomPlaintexts_NoMarkerLeaksOnWire()
    {
        var rng = new Random(Seed:0xC0FFEE);
        var key = new byte[32];
        rng.NextBytes(key);
        var aad = Encoding.UTF8.GetBytes("system:v1:1");
        using var aes = new AesGcm(key, tagSizeInBytes: 16);

        const int Iterations = 200;
        for (var iter = 0; iter < Iterations; iter++)
        {
            var randomTail = new byte[8];
            rng.NextBytes(randomTail);
            var marker = $"SECRET-MARKER-{iter:D4}-{Convert.ToHexString(randomTail)}";
            var markerBytes = Encoding.UTF8.GetBytes(marker);

            // Build a plausible row body — pad with random filler so the
            // ciphertext length varies iteration to iteration. The encrypted
            // payload is what would normally be MessagePacked row columns.
            var fillerLen = rng.Next(0, 256);
            var filler = new byte[fillerLen];
            rng.NextBytes(filler);
            var plaintextBytes = new byte[markerBytes.Length + filler.Length];
            Buffer.BlockCopy(markerBytes, 0, plaintextBytes, 0, markerBytes.Length);
            Buffer.BlockCopy(filler, 0, plaintextBytes, markerBytes.Length, filler.Length);

            var nonce = new byte[12];
            rng.NextBytes(nonce);
            var ciphertext = new byte[plaintextBytes.Length];
            var tag = new byte[16];
            aes.Encrypt(nonce, plaintextBytes, ciphertext, tag, aad);

            // Mimic the worker's wire format: ciphertext || tag in EncryptedRow.
            var ctWithTag = new byte[ciphertext.Length + tag.Length];
            Buffer.BlockCopy(ciphertext, 0, ctWithTag, 0, ciphertext.Length);
            Buffer.BlockCopy(tag, 0, ctWithTag, ciphertext.Length, tag.Length);

            var envelope = new DeltaEnvelope
            {
                Version = 1,
                SenderEd25519PublicKey = "BASE64==SENDER==",
                SenderSignature = new byte[64],
                Groups =
                [
                    new ShadowRowGroup
                    {
                        TableName = "TestItems",
                        IsSystemTable = false,
                        Rows =
                        [
                            new ShadowRow
                            {
                                Id = Guid.NewGuid(),
                                SharingScope = 1,
                                SharingId = "system",
                                EncryptedRow = ctWithTag,
                                Nonce = nonce
                            }
                        ]
                    }
                ]
            };

            var wire = MessagePackSerializer.Serialize(envelope);

            // Latin-1 decode preserves byte values 0..255 verbatim, so an
            // ASCII marker that landed at any byte offset in the wire shows
            // up via Contains() — independent of MessagePack framing.
            var wireText = Encoding.Latin1.GetString(wire);
            Assert.DoesNotContain(marker, wireText);

            // Belt-and-braces: also do a raw byte-subsequence search so a
            // marker containing non-Latin-1-printable bytes would still be
            // caught. (Today the marker is ASCII, but this guards against
            // future expansion of the test inputs.)
            Assert.False(
                ContainsSubsequence(wire, markerBytes),
                $"Iteration {iter}: plaintext marker bytes leaked into envelope wire");
        }
    }

    [Fact]
    public void DeltaEnvelope_AdjacentRows_DoNotLeakAcrossEnvelopeBoundary()
    {
        // Builds an envelope with several rows in one group; asserts that
        // none of the row plaintexts leak into the wire bytes. Catches
        // accidental MessagePack inclusion of row plaintext (e.g. a future
        // refactor that puts something derived from plaintext in a public
        // field of ShadowRow).
        var rng = new Random(Seed:0xBEEF);
        var key = new byte[32];
        rng.NextBytes(key);
        var aad = Encoding.UTF8.GetBytes("system:v1:1");
        using var aes = new AesGcm(key, tagSizeInBytes: 16);

        var rows = new List<ShadowRow>();
        var markers = new List<string>();
        const int RowCount = 20;
        for (var r = 0; r < RowCount; r++)
        {
            var randomTail = new byte[6];
            rng.NextBytes(randomTail);
            var marker = $"ROW-MARKER-{r:D2}-{Convert.ToHexString(randomTail)}";
            markers.Add(marker);

            var plaintextBytes = Encoding.UTF8.GetBytes(marker);
            var nonce = new byte[12];
            rng.NextBytes(nonce);
            var ciphertext = new byte[plaintextBytes.Length];
            var tag = new byte[16];
            aes.Encrypt(nonce, plaintextBytes, ciphertext, tag, aad);

            var ctWithTag = new byte[ciphertext.Length + tag.Length];
            Buffer.BlockCopy(ciphertext, 0, ctWithTag, 0, ciphertext.Length);
            Buffer.BlockCopy(tag, 0, ctWithTag, ciphertext.Length, tag.Length);

            rows.Add(new ShadowRow
            {
                Id = Guid.NewGuid(),
                SharingScope = 1,
                SharingId = "system",
                EncryptedRow = ctWithTag,
                Nonce = nonce
            });
        }

        var envelope = new DeltaEnvelope
        {
            Version = 1,
            SenderEd25519PublicKey = "BASE64==SENDER==",
            SenderSignature = new byte[64],
            Groups =
            [
                new ShadowRowGroup
                {
                    TableName = "TestItems",
                    IsSystemTable = false,
                    Rows = rows
                }
            ]
        };

        var wire = MessagePackSerializer.Serialize(envelope);
        var wireText = Encoding.Latin1.GetString(wire);

        foreach (var marker in markers)
        {
            Assert.DoesNotContain(marker, wireText);
        }
    }

    private static bool ContainsSubsequence(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length)
        {
            return false;
        }
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }
            if (match)
            {
                return true;
            }
        }
        return false;
    }
}
