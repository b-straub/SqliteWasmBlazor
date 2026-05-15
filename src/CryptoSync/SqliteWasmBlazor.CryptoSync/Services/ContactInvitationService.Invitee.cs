using SqliteWasmBlazor.Crypto.Abstractions;
using SqliteWasmBlazor.Crypto.Abstractions.Models;
using MessagePack;

namespace SqliteWasmBlazor.CryptoSync;

// Invitee-side: respond to an admin's invitation bundle by encrypting a
// signed response under HKDF(ECDH(transportPriv, adminPub), info=groupContext)
// and broadcasting it through the sync transport.
internal partial class ContactInvitationService
{
    /// <summary>
    /// Contact-side: respond to an admin's invitation. Verifies the bundle's
    /// admin signature + expiry, derives the transport keypair from the
    /// shared secret, generates the contact's self-group rows locally, signs
    /// the canonical
    /// <c>InvitationId || ContactX25519 || ContactEd25519 || ExpiresAt.Ticks</c>
    /// payload with the contact's Ed25519 key, AES-GCM-encrypts the response
    /// under <c>HKDF(ECDH(transportPriv, adminX25519Pub), info=invitationGroupContext)</c>,
    /// and broadcasts the envelope via <paramref name="syncTransport"/>. The
    /// admin claims it through <see cref="IngestInvitationResponsesAsync"/>;
    /// other broadcast readers fail to unwrap and drop silently.
    /// </summary>
    public async ValueTask RespondToInvitationAsync(
        InvitationBundle bundle,
        DualKeyPairFull contactKeys,
        ContactUserData userData,
        ISyncTransport syncTransport,
        Guid? proposedContactId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(contactKeys);
        ArgumentNullException.ThrowIfNull(syncTransport);

        // 1. Derive both transport keypairs from the shared secret. The
        // X25519 part drives the ECDH for AES-GCM-encrypting the response
        // payload; the Ed25519 part is what the invitee's relay POST signer
        // will use during the bootstrap window (whitelisted by admin).
        var transportDual = await crypto.DeriveDualKeyPairAsync(bundle.TransportSecret).ConfigureAwait(false);
        try
        {
            var transportPub = transportDual.X25519PublicKey;

            // 2. Verify admin's signature on the bundle.
            var canonicalBundle = BuildBundleCanonical(transportPub, bundle.GroupId, bundle.ExpiresAt);
            var bundleOk = await crypto.VerifyAsync(
                canonicalBundle,
                Convert.ToBase64String(bundle.AdminSignature),
                bundle.AdminEd25519PublicKey).ConfigureAwait(false);
            if (!bundleOk)
            {
                throw new InvalidInvitationBundleException(
                    "InvitationBundle.AdminSignature failed Ed25519 verification.");
            }

            // 3. Verify expiry.
            if (DateTime.UtcNow >= bundle.ExpiresAt)
            {
                throw new InvitationExpiredException(
                    $"InvitationBundle expired at {bundle.ExpiresAt:O}.");
            }

            // 4. Build the contact's self-group rows (privacy invariant — admin can't unwrap).
            var contactId = proposedContactId ?? Guid.NewGuid();
            var selfMaterial = await BuildContactSelfGroupAsync(contactKeys, contactId).ConfigureAwait(false);
            var selfGroupId = Guid.NewGuid();

            // 5. Sign canonical (InvitationId || ContactX25519 || ContactEd25519 || ExpiresAt.Ticks).
            var canonicalContact = BuildContactSignatureCanonical(
                bundle.GroupId, contactKeys.X25519PublicKey, contactKeys.Ed25519PublicKey, bundle.ExpiresAt);
            var contactEd25519Priv = contactKeys.Ed25519PrivateKey;
            byte[] contactSig;
            try
            {
                var signResult = await crypto.SignAsync(canonicalContact, contactEd25519Priv).ConfigureAwait(false);
                if (!signResult.Success || signResult.Value is null)
                {
                    throw new InvalidOperationException(
                        $"ContactInvitationService.RespondToInvitationAsync: SignAsync failed: {signResult.ErrorCode}");
                }
                contactSig = Convert.FromBase64String(signResult.Value);
            }
            finally
            {
            }

            var payload = new InvitationResponsePayload
            {
                ContactX25519PublicKey = contactKeys.X25519PublicKey,
                ContactEd25519PublicKey = contactKeys.Ed25519PublicKey,
                SelfGroupId = selfGroupId,
                SelfGroupContext = selfMaterial.GroupContext,
                SelfKeyVersion = selfMaterial.KeyVersion,
                SelfWrappedContentKey = selfMaterial.WrappedCek,
                SelfShareTargetSignature = selfMaterial.ShareTargetSignature,
                ContactSignature = contactSig
            };
            var payloadBytes = MessagePackSerializer.Serialize(payload);

            // 6. AES-GCM under HKDF(ECDH(transportPriv, adminPub), info=invitationGroupContext).
            var groupContext = $"invitation-{bundle.GroupId:N}:v1";
            var transportPriv = transportDual.X25519PrivateKey;
            SymmetricEncryptedData encrypted;
            try
            {
                var wkResult = await crypto.DeriveWrappingKeyAsync(
                    transportPriv, bundle.AdminX25519PublicKey, groupContext).ConfigureAwait(false);
                if (!wkResult.Success)
                {
                    throw new InvalidOperationException(
                        $"ContactInvitationService.RespondToInvitationAsync: DeriveWrappingKeyAsync failed: {wkResult.ErrorCode}");
                }
                try
                {
                    var encResult = await crypto.EncryptSymmetricAsync(
                        Convert.ToBase64String(payloadBytes), wkResult.Value).ConfigureAwait(false);
                    if (!encResult.Success || encResult.Value is null)
                    {
                        throw new InvalidOperationException(
                            $"ContactInvitationService.RespondToInvitationAsync: EncryptSymmetricAsync failed: {encResult.ErrorCode}");
                    }
                    encrypted = encResult.Value;
                }
                finally
                {
                    ClearWrappingKey(wkResult.Value);
                }
            }
            finally
            {
            }

            // 7. Build envelope + broadcast through the transport. The admin
            // ingests via IngestInvitationResponsesAsync; non-admins drop on
            // unwrap-fail since the AES-GCM key is HKDF(ECDH(transport, admin)).
            var envelope = new InvitationResponseEnvelope
            {
                GroupId = bundle.GroupId,
                Ciphertext = Convert.FromBase64String(encrypted.Ciphertext),
                Nonce = Convert.FromBase64String(encrypted.Nonce)
            };
            var envelopeBytes = MessagePackSerializer.Serialize(envelope);

            await syncTransport.SendAsync(envelopeBytes, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            transportDual.Clear();
        }
    }
}
