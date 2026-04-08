using System.Security.Cryptography;
using BlazorPRF.Crypto.Abstractions;
using BlazorPRF.Crypto.Abstractions.Models;
using Microsoft.EntityFrameworkCore;

namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// First-launch scaffolding for a CryptoSync admin instance. Idempotent.
///
/// <para>
/// On a fresh device, this creates everything needed for the local instance
/// to be a functional admin: a <see cref="DeviceSettings"/> row marked
/// <c>IsAdmin=true</c>, the admin's own <see cref="TrustedContact"/> at
/// <see cref="TrustLevel.Full"/>, and a self-<see cref="SharingKey"/> for the
/// system scope holding the deterministically-derived system content key
/// ECIES-wrapped under the admin's own X25519 public key.
/// </para>
///
/// <para>
/// The system content key is NOT stored as a derivable secret — only the
/// wrapped form is at rest. The admin can re-derive it any time via
/// <see cref="KeyDerivation.DeriveSystemContentKey"/> using their own
/// private key, OR unwrap it from their self-SharingKey row. Both paths
/// produce the same key (decision §15 + §16 — explicit self-ShareTarget for
/// uniform runtime lookup).
/// </para>
///
/// <para>
/// Idempotent contract: if a DeviceSettings row already exists with
/// <c>IsAdmin=true</c> AND a TrustedContact for the same Ed25519 public key
/// exists, the call returns the existing admin contact without modification.
/// Re-running the bootstrap on an already-initialized device is a no-op.
/// </para>
/// </summary>
public class CryptoSyncBootstrap(
    CryptoSyncContextBase context,
    ICryptoProvider crypto)
{
    /// <summary>
    /// Initialize this device as the admin instance.
    /// </summary>
    /// <param name="adminKeys">Admin's full key pair (Ed25519 for signing, X25519 for ECIES).</param>
    /// <param name="adminUsername">Display name for the admin contact row.</param>
    /// <param name="adminEmail">Email for the admin contact row.</param>
    /// <param name="deviceName">Friendly device name for the DeviceSettings row.</param>
    /// <returns>The admin's TrustedContact row (newly created or existing).</returns>
    public async ValueTask<TrustedContact> InitializeAdminAsync(
        DualKeyPairFull adminKeys,
        string adminUsername,
        string adminEmail,
        string deviceName)
    {
        // ---- Idempotency check ----
        var existingDevice = await context.DeviceSettings.FirstOrDefaultAsync();
        if (existingDevice is { IsAdmin: true })
        {
            var existingAdmin = await context.Contacts
                .FirstOrDefaultAsync(c => c.Ed25519PublicKey == adminKeys.Ed25519PublicKey);
            if (existingAdmin is not null)
            {
                return existingAdmin;
            }
        }

        var now = DateTime.UtcNow;

        // ---- 1. DeviceSettings ----
        // Either create fresh, or upgrade an existing non-admin row.
        if (existingDevice is null)
        {
            existingDevice = new DeviceSettings
            {
                Id = Guid.NewGuid(),
                ClientGuid = Guid.NewGuid().ToString(),
                DeviceName = deviceName,
                IsAdmin = true
            };
            context.DeviceSettings.Add(existingDevice);
        }
        else
        {
            existingDevice.IsAdmin = true;
            existingDevice.DeviceName = deviceName;
        }

        // ---- 2. Admin's own TrustedContact row ----
        // Public-scope, system SharingId so this contact will be broadcast to
        // every Full-trust peer once they're added to the system scope.
        var adminContact = new TrustedContact
        {
            Id = Guid.NewGuid(),
            Username = adminUsername,
            Email = adminEmail,
            X25519PublicKey = adminKeys.X25519PublicKey,
            Ed25519PublicKey = adminKeys.Ed25519PublicKey,
            Role = SyncRole.Owner,
            TrustLevel = TrustLevel.Full,
            Direction = TrustDirection.Sent,
            VerifiedAt = now,
            UpdatedAt = now,
            SharingScope = SharingScope.Public,
            SharingId = KeyDerivation.SystemSharingId
        };
        context.Contacts.Add(adminContact);

        // Link DeviceSettings to the admin contact id (lets non-admin peers
        // later resolve "which contact is the admin" via DeviceSettings.AdminContactId).
        existingDevice.AdminContactId = adminContact.Id;

        // ---- 3. Derive the system content key ----
        // Deterministic from admin's private key — no storage needed for the
        // key itself. The wrapped form goes into the self-SharingKey below
        // (decision §16 — uniform lookup, owner has an explicit ShareTarget).
        var adminPrivateKeyBytes = Convert.FromBase64String(adminKeys.X25519PrivateKey);
        byte[]? systemContentKey = null;

        try
        {
            systemContentKey = KeyDerivation.DeriveSystemContentKey(adminPrivateKeyBytes);

            // ---- 4. Self-SharingKey for the system scope ----
            // Wrap the freshly-derived content key under admin's OWN X25519
            // public key. Admin can either re-derive (via KeyDerivation) or
            // ECIES-unwrap from this row — both yield the same key.
            var contentKeyBase64 = Convert.ToBase64String(systemContentKey);
            var wrapResult = await crypto.EncryptAsymmetricAsync(contentKeyBase64, adminKeys.X25519PublicKey);
            if (!wrapResult.Success)
            {
                throw new InvalidOperationException(
                    $"Failed to wrap system content key for admin self-SharingKey: {wrapResult.ErrorCode}");
            }

            var wrappedKeyBytes = EnvelopeBytes.Serialize(wrapResult.Value!);

            var selfSharingKey = new SharingKey
            {
                Id = Guid.NewGuid(),
                SharingId = KeyDerivation.SystemSharingId,
                SharingScope = SharingScope.Public,
                ClientContactId = adminContact.Id,
                WrappedContentKey = wrappedKeyBytes,
                Role = SyncRole.Owner,
                GrantedByContactId = adminContact.Id, // admin granted to themselves
                CreatedAt = now
            };
            context.SharingKeys.Add(selfSharingKey);

            await context.SaveChangesAsync();
        }
        finally
        {
            // Zero key material from this method's stack copies. The CryptoKey
            // handle inside ICryptoProvider has its own lifetime.
            if (systemContentKey is not null)
            {
                CryptographicOperations.ZeroMemory(systemContentKey);
            }
            CryptographicOperations.ZeroMemory(adminPrivateKeyBytes);
        }

        return adminContact;
    }
}
