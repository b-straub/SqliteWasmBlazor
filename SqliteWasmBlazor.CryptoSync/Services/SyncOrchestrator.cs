using System.Security.Cryptography;
using BlazorPRF.Crypto.Abstractions;
using BlazorPRF.Crypto.Abstractions.Models;
using MessagePack;

namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// Thin bridge between the C# domain layer and the worker's encrypted bulk
/// import/export. The worker owns AES-GCM symmetric crypto on the V2 payload
/// and (after Phase D-3) in-transaction permission enforcement during apply.
///
/// <para>
/// C# is responsible for:
/// </para>
/// <list type="bullet">
///   <item>ECIES content-key wrap/unwrap (X25519 — uses <see cref="ICryptoProvider"/>
///         which carries the user's private key in managed memory).</item>
///   <item>Ed25519 content signature (sender proves they produced the data).</item>
///   <item>Envelope assembly (<see cref="EncryptedDelta"/>) — MessagePack-serialized
///         transport format.</item>
///   <item>Recipient discovery via <see cref="ContactService"/>.</item>
/// </list>
///
/// <para>
/// Permissions are not shipped in the envelope (decision §6). The worker enforces
/// them by querying the locally-applied <c>SyncPermission</c> table during apply.
/// </para>
/// </summary>
public class SyncOrchestrator(
    ISqliteWasmDatabaseService databaseService,
    ICryptoProvider crypto,
    ContactService contactService)
{
    /// <summary>
    /// Export data as an encrypted delta for all contacts plus self (round-trip).
    /// </summary>
    public async ValueTask<byte[]> ExportAsync(
        string databaseName,
        BulkExportMetadata exportMetadata,
        DualKeyPairFull senderKeys)
    {
        // 1. Fresh content key — zeroed before this method returns.
        var contentKey = new byte[32];
        RandomNumberGenerator.Fill(contentKey);

        try
        {
            // 2. Worker reads the open table, encrypts V2 bytes with the content key,
            //    returns (ciphertext, nonce). Plain V2 bytes never leave the worker.
            var (ciphertext, nonce) = await databaseService.BulkExportEncryptedAsync(
                databaseName, exportMetadata, contentKey);

            // 3. Sign the ciphertext with the sender's Ed25519 key.
            var ciphertextBase64 = Convert.ToBase64String(ciphertext);
            var senderEd25519Private = Convert.FromBase64String(senderKeys.Ed25519PrivateKey);
            var signResult = await crypto.SignAsync(ciphertextBase64, senderEd25519Private);
            if (!signResult.Success)
            {
                throw new InvalidOperationException($"Signing failed: {signResult.ErrorCode}");
            }
            var contentSignature = Convert.FromBase64String(signResult.Value!);

            // 4. Recipient list = all known contacts + self (so the sender can decrypt
            //    their own outgoing data on a future device sync).
            var recipientPks = await contactService.GetRecipientPublicKeysAsync();
            var allRecipients = recipientPks.Append(senderKeys.X25519PublicKey).Distinct().ToArray();

            // 5. ECIES-wrap the content key for each recipient.
            var recipientEnvelopes = new Dictionary<string, byte[]>();
            var contentKeyBase64 = Convert.ToBase64String(contentKey);
            foreach (var recipientPk in allRecipients)
            {
                var wrapResult = await crypto.EncryptAsymmetricAsync(contentKeyBase64, recipientPk);
                if (!wrapResult.Success)
                {
                    throw new InvalidOperationException($"Key wrapping failed: {wrapResult.ErrorCode}");
                }
                recipientEnvelopes[recipientPk] = EnvelopeBytes.Serialize(wrapResult.Value!);
            }

            // 6. Assemble + serialize the envelope.
            var delta = new EncryptedDelta
            {
                Ciphertext = ciphertext,
                Nonce = nonce,
                ContentSignature = contentSignature,
                SenderPublicKey = senderKeys.Ed25519PublicKey,
                RecipientEnvelopes = recipientEnvelopes
            };
            return MessagePackSerializer.Serialize(delta);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(contentKey);
        }
    }

    /// <summary>
    /// Import an encrypted delta: verify the sender, ECIES-unwrap our content key,
    /// hand the worker the encrypted payload + key for in-worker decrypt + apply.
    /// </summary>
    public async ValueTask<int> ImportAsync(
        string databaseName,
        byte[] envelopeBytes,
        DualKeyPairFull recipientKeys,
        ConflictResolutionStrategy conflictStrategy = ConflictResolutionStrategy.DeltaWins)
    {
        // 1. Deserialize envelope.
        var delta = MessagePackSerializer.Deserialize<EncryptedDelta>(envelopeBytes);

        // 2. Verify sender is a known contact.
        var senderContact = await contactService.GetByEd25519PublicKeyAsync(delta.SenderPublicKey);
        if (senderContact is null)
        {
            throw new InvalidOperationException($"Unknown sender: {delta.SenderPublicKey[..16]}...");
        }

        // 3. Verify content signature.
        var ciphertextBase64 = Convert.ToBase64String(delta.Ciphertext);
        var signatureBase64 = Convert.ToBase64String(delta.ContentSignature);
        var isValid = await crypto.VerifyAsync(ciphertextBase64, signatureBase64, delta.SenderPublicKey);
        if (!isValid)
        {
            throw new InvalidOperationException("Content signature verification failed");
        }

        // 4. Find our wrapped key.
        if (!delta.RecipientEnvelopes.TryGetValue(recipientKeys.X25519PublicKey, out var wrappedKeyBytes))
        {
            throw new InvalidOperationException("Delta not encrypted for this recipient");
        }

        // 5. ECIES-unwrap the content key.
        var encryptedMsg = EnvelopeBytes.Deserialize(wrappedKeyBytes);
        var recipientPrivateKey = Convert.FromBase64String(recipientKeys.X25519PrivateKey);
        var unwrapResult = await crypto.DecryptAsymmetricAsync(encryptedMsg, recipientPrivateKey);
        if (!unwrapResult.Success)
        {
            throw new InvalidOperationException($"Key unwrapping failed: {unwrapResult.ErrorCode}");
        }

        var contentKey = Convert.FromBase64String(unwrapResult.Value!);

        try
        {
            // 6. Worker decrypts the payload with the content key and applies into the
            //    open table (and the _crypto_ shadow). Permission enforcement happens
            //    inside the worker after Phase D-3 lands.
            return await databaseService.BulkImportEncryptedAsync(
                databaseName, delta.Ciphertext, delta.Nonce, contentKey, conflictStrategy);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(contentKey);
        }
    }

    // Envelope serialization helpers moved to EnvelopeBytes — shared with
    // CryptoSyncBootstrap (writes admin's self-SharingKey) and any future
    // ECIES consumer that needs the wire format.
}
