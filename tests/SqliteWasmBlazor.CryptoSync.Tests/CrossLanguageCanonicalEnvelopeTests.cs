using SqliteWasmBlazor.Crypto.Abstractions.Models;
using SqliteWasmBlazor.Crypto.Abstractions.Services;
using Xunit;

namespace SqliteWasmBlazor.CryptoSync.Tests;

// Cross-language byte-equality vector for GroupEncryptionService.BuildCanonicalEnvelope
// and the TS counterpart `buildCanonicalEnvelope` in
// `crypto-core/src/group.ts`.
//
// Both sides hash the raw ciphertext bytes (C# decodes the base64 string
// first, TS already carries `Uint8Array`) and produce the same canonical
// string for the same logical inputs. Cross-language sign/verify
// round-trips depend on this agreement.
//
// Mirrored at `crypto-core/tests/crossLanguageEnvelope.test.ts` —
// the golden value is identical on both sides.
public class CrossLanguageCanonicalEnvelopeTests
{
    private const string GroupContext = "group-test:v1";
    private const int KeyVersion = 1;

    // Sender Ed25519 public key — 32 bytes, all 0x42.
    private static readonly byte[] SenderPublicKeyBytes = Enumerable.Repeat((byte)0x42, 32).ToArray();
    private static readonly string SenderPublicKeyBase64 = Convert.ToBase64String(SenderPublicKeyBytes);

    // Ciphertext + nonce — fixed bytes so the envelope's SHA-256 is deterministic.
    private static readonly byte[] CiphertextBytes = "Hello, World!\0\0\0"u8.ToArray();
    private static readonly byte[] NonceBytes = Enumerable.Range(0, 12).Select(i => (byte)i).ToArray();
    private static readonly string CiphertextBase64 = Convert.ToBase64String(CiphertextBytes);
    private static readonly string NonceBase64 = Convert.ToBase64String(NonceBytes);

    // Unified golden envelope string — identical on the C# and TS sides.
    private const string ExpectedCanonicalEnvelope =
        "group-test:v1|1|QkJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCQkI=|vBNtBKM0PskIjlUP4MprfsH8qPedWnz9gPHybmtEpAQ=";

    [Fact]
    public void BuildCanonicalEnvelope_MatchesGolden()
    {
        var encrypted = new SymmetricEncryptedData(CiphertextBase64, NonceBase64);

        var actual = GroupEncryptionService.BuildCanonicalEnvelope(
            GroupContext, KeyVersion, SenderPublicKeyBase64, encrypted);

        Assert.Equal(ExpectedCanonicalEnvelope, actual);
    }

    [Fact]
    public void BuildCanonicalEnvelope_IsDeterministic()
    {
        var encrypted = new SymmetricEncryptedData(CiphertextBase64, NonceBase64);

        var first = GroupEncryptionService.BuildCanonicalEnvelope(
            GroupContext, KeyVersion, SenderPublicKeyBase64, encrypted);
        var second = GroupEncryptionService.BuildCanonicalEnvelope(
            GroupContext, KeyVersion, SenderPublicKeyBase64, encrypted);

        Assert.Equal(first, second);
    }
}
