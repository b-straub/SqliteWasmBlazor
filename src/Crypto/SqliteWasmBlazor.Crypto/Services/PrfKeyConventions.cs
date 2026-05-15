namespace SqliteWasmBlazor.Crypto.Services;

/// <summary>
/// Single source of truth for the JS-side key cache identifier shared by
/// <see cref="PrfService"/> (which writes the entry via
/// <c>ICryptoProvider.StoreKeysAsync</c>) and the consumers that read it
/// (<see cref="SigningService"/>).
///
/// <para>
/// JS-side <c>keyCache</c> stores the derived Ed25519 + AES keys as
/// non-extractable <c>SubtleCrypto</c> CryptoKey objects under a single key
/// identifier per PRF salt — so all consumers must compute the same id from
/// the same salt, otherwise the cache lookup misses and operations fall back
/// to errors.
/// </para>
/// </summary>
internal static class PrfKeyConventions
{
    /// <summary>
    /// JS-side key cache identifier for the dual (X25519 + Ed25519 + AES)
    /// key bundle derived from a PRF salt.
    /// </summary>
    public static string GetJsKeyId(string salt) => $"prf-keys:{salt}";
}
