using System.Runtime.InteropServices;
using SqliteWasmBlazor.Crypto.Abstractions;
using SqliteWasmBlazor.Crypto.Abstractions.Models;

namespace SqliteWasmBlazor.Crypto.Interop;

/// <summary>
/// Canonical async-bridge shape for byte-shaped JSImport encrypt operations
/// whose output is a non-secret packed Base64 structure
/// (e.g. <c>[ephPubKey|nonce|ciphertext]</c>).
///
/// <para>
/// Used by <c>EncryptAsymmetricFromBytesAsync</c>. Bytes-out paths (where
/// the JSImport return would carry secret material) instead use writable
/// <c>[JSMarshalAs&lt;MemoryView&gt;]</c> output parameters directly — see
/// <c>CryptoInterop.DecryptAsymmetricIntoAsync</c> / <c>DecryptAesGcmIntoAsync</c> /
/// <c>DeriveDualKeyPairInto</c> / <c>DeriveWrappingKeyInto</c> / etc. Those
/// paths don't need a helper because the caller pre-allocates the output
/// buffer and the JSImport writes into it directly — no Base64 round-trip
/// to decode.
/// </para>
/// </summary>
internal static class BridgeAsync
{
    /// <summary>
    /// Bytes-in / Base64-out async bridge — JSImport returns Base64 of an
    /// opaque (non-secret) packed structure that the caller unpacks. Used
    /// for encrypt operations where the output (eph pubkey + nonce +
    /// ciphertext) is not secret-bearing on its own.
    /// </summary>
    public static async ValueTask<PrfResult<string>> BytesInBase64Out(
        ReadOnlyMemory<byte> input,
        Func<ArraySegment<byte>, Task<string>> jsCall,
        PrfErrorCode failureCode)
    {
        if (!MemoryMarshal.TryGetArray(input, out ArraySegment<byte> inputSegment))
        {
            return PrfResult<string>.Fail(failureCode);
        }

        try
        {
            return PrfResult<string>.Ok(await jsCall(inputSegment));
        }
        catch
        {
            return PrfResult<string>.Fail(failureCode);
        }
    }
}
