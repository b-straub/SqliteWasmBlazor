using SqliteWasmBlazor.Crypto.Abstractions.Models;

namespace SqliteWasmBlazor.Crypto.Services;

/// <summary>
/// Service for symmetric encryption using AES-256-GCM.
/// </summary>
public interface ISymmetricEncryption
{
    /// <summary>
    /// Encrypt a message using the provided or cached symmetric key.
    /// </summary>
    /// <param name="message">The plaintext message</param>
    /// <param name="keyIdentifier">Identifier for the key (salt for PRF, key ID for PseudoPRF)</param>
    /// <returns>The encrypted message or error</returns>
    ValueTask<PrfResult<SymmetricEncryptedData>> EncryptAsync(string message, string keyIdentifier);

    /// <summary>
    /// Decrypt a message using the provided or cached symmetric key.
    /// </summary>
    /// <param name="encrypted">The encrypted message</param>
    /// <param name="keyIdentifier">Identifier for the key (salt for PRF, key ID for PseudoPRF)</param>
    /// <returns>The decrypted plaintext or error</returns>
    ValueTask<PrfResult<string>> DecryptAsync(SymmetricEncryptedData encrypted, string keyIdentifier);
}
