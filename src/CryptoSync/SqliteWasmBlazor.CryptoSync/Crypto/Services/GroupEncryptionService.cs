using System.Security.Cryptography;
using System.Text;
using SqliteWasmBlazor.Crypto.Abstractions.Models;

namespace SqliteWasmBlazor.Crypto.Abstractions.Services;

/// <summary>
/// Group encryption service — pure composition of ICryptoProvider primitives.
/// Provider-agnostic: works with any ICryptoProvider implementation (Noble.js, BouncyCastle, etc.).
/// </summary>
public sealed class GroupEncryptionService(ICryptoProvider cryptoProvider) : IGroupEncryption
{
    // ============================================================
    // CONTROL PLANE (Admin only)
    // ============================================================

    public async ValueTask<PrfResult<GroupKeyBundle>> CreateGroupKeysAsync(
        ReadOnlyMemory<byte> adminPrivateKey,
        string adminPublicKey,
        IReadOnlyList<string> memberPublicKeys,
        string groupContext)
    {
        // Generate random CEK
        var cek = await cryptoProvider.GenerateContentKeyAsync();

        try
        {
            // Wrap CEK for each member
            var memberKeys = new List<WrappedKey>(memberPublicKeys.Count);

            foreach (var memberPubKey in memberPublicKeys)
            {
                var wrappingKeyResult = await cryptoProvider.DeriveWrappingKeyAsync(adminPrivateKey, memberPubKey, groupContext);
                if (!wrappingKeyResult.Success || wrappingKeyResult.Value.Length == 0)
                {
                    return PrfResult<GroupKeyBundle>.Fail(PrfErrorCode.KEY_DERIVATION_FAILED);
                }

                try
                {
                    var wrapResult = await cryptoProvider.WrapContentKeyAsync(cek, wrappingKeyResult.Value);
                    if (!wrapResult.Success || wrapResult.Value is null)
                    {
                        return PrfResult<GroupKeyBundle>.Fail(PrfErrorCode.ENCRYPTION_FAILED);
                    }

                    memberKeys.Add(new WrappedKey(memberPubKey, wrapResult.Value));
                }
                finally
                {
                    ClearMemory(wrappingKeyResult.Value);
                }
            }

            var bundle = new GroupKeyBundle(groupContext, 1, adminPublicKey, memberKeys);
            return PrfResult<GroupKeyBundle>.Ok(bundle);
        }
        finally
        {
            // Clear CEK from memory
            ClearMemory(cek);
        }
    }

    public async ValueTask<PrfResult<IReadOnlyList<WrappedKey>>> AddGroupMembersAsync(
        ReadOnlyMemory<byte> adminPrivateKey,
        string adminPublicKey,
        SymmetricEncryptedData adminWrappedCek,
        IReadOnlyList<string> newMemberPublicKeys,
        string groupContext)
    {
        // Unwrap admin's CEK
        var cekResult = await UnwrapAdminCekAsync(adminPrivateKey, adminPublicKey, adminWrappedCek, groupContext);
        if (!cekResult.Success || cekResult.Value.Length == 0)
        {
            return PrfResult<IReadOnlyList<WrappedKey>>.Fail(cekResult.ErrorCode ?? PrfErrorCode.DECRYPTION_FAILED);
        }

        try
        {
            // Wrap CEK for each new member
            var newKeys = new List<WrappedKey>(newMemberPublicKeys.Count);

            foreach (var memberPubKey in newMemberPublicKeys)
            {
                var wrappingKeyResult = await cryptoProvider.DeriveWrappingKeyAsync(adminPrivateKey, memberPubKey, groupContext);
                if (!wrappingKeyResult.Success || wrappingKeyResult.Value.Length == 0)
                {
                    return PrfResult<IReadOnlyList<WrappedKey>>.Fail(PrfErrorCode.KEY_DERIVATION_FAILED);
                }

                try
                {
                    var wrapResult = await cryptoProvider.WrapContentKeyAsync(cekResult.Value, wrappingKeyResult.Value);
                    if (!wrapResult.Success || wrapResult.Value is null)
                    {
                        return PrfResult<IReadOnlyList<WrappedKey>>.Fail(PrfErrorCode.ENCRYPTION_FAILED);
                    }

                    newKeys.Add(new WrappedKey(memberPubKey, wrapResult.Value));
                }
                finally
                {
                    ClearMemory(wrappingKeyResult.Value);
                }
            }

            return PrfResult<IReadOnlyList<WrappedKey>>.Ok(newKeys);
        }
        finally
        {
            ClearMemory(cekResult.Value);
        }
    }

    public async ValueTask<PrfResult<GroupKeyBundle>> RotateGroupKeyAsync(
        ReadOnlyMemory<byte> adminPrivateKey,
        string adminPublicKey,
        IReadOnlyList<string> remainingMemberPublicKeys,
        string groupContext)
    {
        // Generate new CEK (old one is discarded — forward secrecy)
        var newCek = await cryptoProvider.GenerateContentKeyAsync();

        try
        {
            // Wrap new CEK for each remaining member
            var memberKeys = new List<WrappedKey>(remainingMemberPublicKeys.Count);

            foreach (var memberPubKey in remainingMemberPublicKeys)
            {
                var wrappingKeyResult = await cryptoProvider.DeriveWrappingKeyAsync(adminPrivateKey, memberPubKey, groupContext);
                if (!wrappingKeyResult.Success || wrappingKeyResult.Value.Length == 0)
                {
                    return PrfResult<GroupKeyBundle>.Fail(PrfErrorCode.KEY_DERIVATION_FAILED);
                }

                try
                {
                    var wrapResult = await cryptoProvider.WrapContentKeyAsync(newCek, wrappingKeyResult.Value);
                    if (!wrapResult.Success || wrapResult.Value is null)
                    {
                        return PrfResult<GroupKeyBundle>.Fail(PrfErrorCode.ENCRYPTION_FAILED);
                    }

                    memberKeys.Add(new WrappedKey(memberPubKey, wrapResult.Value));
                }
                finally
                {
                    ClearMemory(wrappingKeyResult.Value);
                }
            }

            // Extract version from context (format: "group-{id}:v{N}")
            var version = ParseVersionFromContext(groupContext);
            var bundle = new GroupKeyBundle(groupContext, version, adminPublicKey, memberKeys);
            return PrfResult<GroupKeyBundle>.Ok(bundle);
        }
        finally
        {
            ClearMemory(newCek);
        }
    }

    public async ValueTask<PrfResult<GroupKeyBundle>> TransferGroupAdminAsync(
        ReadOnlyMemory<byte> oldAdminPrivateKey,
        string oldAdminPublicKey,
        SymmetricEncryptedData oldAdminWrappedCek,
        ReadOnlyMemory<byte> newAdminPrivateKey,
        string newAdminPublicKey,
        IReadOnlyList<string> memberPublicKeys,
        string groupContext,
        int keyVersion)
    {
        // Old admin unwraps CEK
        var cekResult = await UnwrapAdminCekAsync(oldAdminPrivateKey, oldAdminPublicKey, oldAdminWrappedCek, groupContext);
        if (!cekResult.Success || cekResult.Value.Length == 0)
        {
            return PrfResult<GroupKeyBundle>.Fail(cekResult.ErrorCode ?? PrfErrorCode.DECRYPTION_FAILED);
        }

        try
        {
            // New admin re-wraps for all members using their private key
            var memberKeys = new List<WrappedKey>(memberPublicKeys.Count);

            foreach (var memberPubKey in memberPublicKeys)
            {
                var wrappingKeyResult = await cryptoProvider.DeriveWrappingKeyAsync(newAdminPrivateKey, memberPubKey, groupContext);
                if (!wrappingKeyResult.Success || wrappingKeyResult.Value.Length == 0)
                {
                    return PrfResult<GroupKeyBundle>.Fail(PrfErrorCode.KEY_DERIVATION_FAILED);
                }

                try
                {
                    var wrapResult = await cryptoProvider.WrapContentKeyAsync(cekResult.Value, wrappingKeyResult.Value);
                    if (!wrapResult.Success || wrapResult.Value is null)
                    {
                        return PrfResult<GroupKeyBundle>.Fail(PrfErrorCode.ENCRYPTION_FAILED);
                    }

                    memberKeys.Add(new WrappedKey(memberPubKey, wrapResult.Value));
                }
                finally
                {
                    ClearMemory(wrappingKeyResult.Value);
                }
            }

            var bundle = new GroupKeyBundle(groupContext, keyVersion, newAdminPublicKey, memberKeys);
            return PrfResult<GroupKeyBundle>.Ok(bundle);
        }
        finally
        {
            ClearMemory(cekResult.Value);
        }
    }

    // ============================================================
    // DATA PLANE (Any member)
    // ============================================================

    public async ValueTask<PrfResult<GroupEncryptedData>> EncryptForGroupAsync(
        ReadOnlyMemory<byte> senderX25519PrivateKey,
        ReadOnlyMemory<byte> senderEd25519PrivateKey,
        string senderEd25519PublicKey,
        string adminPublicKey,
        SymmetricEncryptedData senderWrappedCek,
        string plaintext,
        string groupContext,
        int keyVersion)
    {
        // Unwrap sender's CEK
        var wrappingKeyResult = await cryptoProvider.DeriveWrappingKeyAsync(senderX25519PrivateKey, adminPublicKey, groupContext);
        if (!wrappingKeyResult.Success || wrappingKeyResult.Value.Length == 0)
        {
            return PrfResult<GroupEncryptedData>.Fail(PrfErrorCode.KEY_DERIVATION_FAILED);
        }

        PrfResult<ReadOnlyMemory<byte>> cekResult;
        try
        {
            cekResult = await cryptoProvider.UnwrapContentKeyAsync(senderWrappedCek, wrappingKeyResult.Value);
        }
        finally
        {
            ClearMemory(wrappingKeyResult.Value);
        }
        if (!cekResult.Success || cekResult.Value.Length == 0)
        {
            return PrfResult<GroupEncryptedData>.Fail(cekResult.ErrorCode ?? PrfErrorCode.DECRYPTION_FAILED);
        }

        try
        {
            // Encrypt with AAD binding
            var aad = $"{groupContext}:{keyVersion}";
            var encryptResult = await cryptoProvider.EncryptSymmetricAsync(plaintext, cekResult.Value, aad);
            if (!encryptResult.Success || encryptResult.Value is null)
            {
                return PrfResult<GroupEncryptedData>.Fail(PrfErrorCode.ENCRYPTION_FAILED);
            }

            // Sign the canonical envelope
            var envelopePayload = BuildCanonicalEnvelope(
                groupContext, keyVersion, senderEd25519PublicKey, encryptResult.Value);

            var signResult = await cryptoProvider.SignAsync(envelopePayload, senderEd25519PrivateKey);
            if (!signResult.Success || signResult.Value is null)
            {
                return PrfResult<GroupEncryptedData>.Fail(PrfErrorCode.SIGNING_FAILED);
            }

            var message = new GroupEncryptedData(
                groupContext,
                keyVersion,
                encryptResult.Value,
                senderEd25519PublicKey,
                signResult.Value);

            return PrfResult<GroupEncryptedData>.Ok(message);
        }
        finally
        {
            ClearMemory(cekResult.Value);
        }
    }

    public async ValueTask<PrfResult<string>> DecryptFromGroupAsync(
        ReadOnlyMemory<byte> recipientX25519PrivateKey,
        string adminPublicKey,
        SymmetricEncryptedData recipientWrappedCek,
        GroupEncryptedData data)
    {
        // Layer 2: Verify envelope signature
        var envelopePayload = BuildCanonicalEnvelope(
            data.GroupContext, data.KeyVersion, data.SenderPublicKey, data.Encrypted);

        var signatureValid = await cryptoProvider.VerifyAsync(envelopePayload, data.EnvelopeSignature, data.SenderPublicKey);
        if (!signatureValid)
        {
            return PrfResult<string>.Fail(PrfErrorCode.VERIFICATION_FAILED);
        }

        // Layer 3: Unwrap CEK
        var wrappingKeyResult = await cryptoProvider.DeriveWrappingKeyAsync(recipientX25519PrivateKey, adminPublicKey, data.GroupContext);
        if (!wrappingKeyResult.Success || wrappingKeyResult.Value.Length == 0)
        {
            return PrfResult<string>.Fail(PrfErrorCode.KEY_DERIVATION_FAILED);
        }

        PrfResult<ReadOnlyMemory<byte>> cekResult;
        try
        {
            cekResult = await cryptoProvider.UnwrapContentKeyAsync(recipientWrappedCek, wrappingKeyResult.Value);
        }
        finally
        {
            ClearMemory(wrappingKeyResult.Value);
        }
        if (!cekResult.Success || cekResult.Value.Length == 0)
        {
            return PrfResult<string>.Fail(cekResult.ErrorCode ?? PrfErrorCode.DECRYPTION_FAILED);
        }

        try
        {
            // Layer 1: Decrypt with AAD verification
            var aad = $"{data.GroupContext}:{data.KeyVersion}";
            return await cryptoProvider.DecryptSymmetricAsync(data.Encrypted, cekResult.Value, aad);
        }
        finally
        {
            ClearMemory(cekResult.Value);
        }
    }

    // ============================================================
    // HELPERS
    // ============================================================

    private async ValueTask<PrfResult<ReadOnlyMemory<byte>>> UnwrapAdminCekAsync(
        ReadOnlyMemory<byte> adminPrivateKey,
        string adminPublicKey,
        SymmetricEncryptedData adminWrappedCek,
        string groupContext)
    {
        // Admin derives wrapping key using ECDH with themselves (admin priv + admin pub)
        var wrappingKeyResult = await cryptoProvider.DeriveWrappingKeyAsync(adminPrivateKey, adminPublicKey, groupContext);
        if (!wrappingKeyResult.Success || wrappingKeyResult.Value.Length == 0)
        {
            return PrfResult<ReadOnlyMemory<byte>>.Fail(PrfErrorCode.KEY_DERIVATION_FAILED);
        }

        try
        {
            return await cryptoProvider.UnwrapContentKeyAsync(adminWrappedCek, wrappingKeyResult.Value);
        }
        finally
        {
            ClearMemory(wrappingKeyResult.Value);
        }
    }

    /// <summary>
    /// Builds the canonical envelope string for signing/verification.
    /// Includes a SHA-256 hash of the ciphertext to bind the signature to the encrypted content.
    /// </summary>
    private static string BuildCanonicalEnvelope(
        string groupContext,
        int keyVersion,
        string senderPublicKey,
        SymmetricEncryptedData encrypted)
    {
        var ciphertextHash = Convert.ToBase64String(
            SHA256.HashData(Encoding.UTF8.GetBytes(encrypted.Ciphertext)));

        return $"{groupContext}|{keyVersion}|{senderPublicKey}|{ciphertextHash}";
    }

    private static int ParseVersionFromContext(string groupContext)
    {
        // Expected format: "group-{id}:v{N}" or similar with version suffix
        var vIndex = groupContext.LastIndexOf(":v", StringComparison.Ordinal);
        if (vIndex >= 0 && int.TryParse(groupContext[(vIndex + 2)..], out var version))
        {
            return version;
        }

        return 1; // Default version if not parseable
    }

    private static void ClearMemory(ReadOnlyMemory<byte> memory)
    {
        // Best-effort clear — ReadOnlyMemory doesn't guarantee writable backing
        if (System.Runtime.InteropServices.MemoryMarshal.TryGetArray(memory, out var segment) && segment.Array is not null)
        {
            Array.Clear(segment.Array, segment.Offset, segment.Count);
        }
    }
}
