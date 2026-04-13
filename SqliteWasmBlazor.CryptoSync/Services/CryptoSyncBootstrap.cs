using System.Security.Cryptography;
using BlazorPRF.Crypto.Abstractions.Models;
using BlazorPRF.Crypto.Abstractions.Services;

namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// Pre-computed admin seed data — pure crypto output, no DbContext dependency.
/// Contains all the rows needed to bootstrap an admin instance. The consumer
/// writes these to the database (via HasData in OnModelCreating, or direct insert).
/// </summary>
public sealed class AdminSeedData
{
    public required TrustedContact AdminContact { get; init; }
    public required ShareGroup SystemGroup { get; init; }
    public required ShareTarget AdminShareTarget { get; init; }

    /// <summary>
    /// Admin's private self-group (<see cref="SharingScope.CLIENT"/>). Holds
    /// the CEK for any <c>Client</c>-scoped row the admin device creates. Wrapped
    /// via <c>HKDF(ECDH(adminPriv, adminPub), info=BuildSelfGroupContext(adminContactId))</c>
    /// — a key only the admin can re-derive. The row itself is routed
    /// through the system CEK (<see cref="SharingId"/> = <c>"system"</c>) so
    /// it can travel via the system table sync envelope.
    /// </summary>
    public required ShareGroup AdminSelfGroup { get; init; }

    /// <summary>
    /// Admin's wrapped self-CEK for the self-group above. Sole member of the
    /// self-group; <see cref="ShareTarget.Role"/> = <see cref="SyncRole.OWNER"/>.
    /// </summary>
    public required ShareTarget AdminSelfTarget { get; init; }

    public required DeviceSettings Device { get; init; }
}

/// <summary>
/// Pure crypto bootstrap — takes keys in, produces <see cref="AdminSeedData"/> out.
/// No DbContext dependency. The consumer decides where to store the seed
/// (HasData in OnModelCreating, direct insert, or serialized .cs file).
///
/// <para>
/// For testing: console app calls <see cref="CreateAdminSeedAsync"/> with
/// hardcoded keys and emits a .cs file.
/// For production: WebApp calls the same method with PRF-derived keys.
/// </para>
/// </summary>
public class CryptoSyncBootstrap(IGroupEncryption groupEncryption, DeclarationSigner signer)
{
    /// <summary>Well-known group context for the system scope (v1).</summary>
    public const string SystemGroupContext = "system:v1";

    /// <summary>Well-known SharingId for the system scope.</summary>
    public const string SystemSharingId = "system";

    /// <summary>
    /// Build the canonical self-group <see cref="ShareGroup.GroupContext"/>
    /// string for a contact. Used as (a) the HKDF info parameter when wrapping
    /// the self-CEK, (b) the value the contact's domain rows carry as
    /// <see cref="SyncableEntity.SharingId"/> so the worker's
    /// <c>sg.GroupContext = c.SharingId</c> join resolves.
    /// </summary>
    public static string BuildSelfGroupContext(Guid contactId, int keyVersion = 1)
        => $"self-{contactId:N}:v{keyVersion}";

    /// <summary>
    /// Create the admin seed: TrustedContact + ShareGroup + ShareTarget + DeviceSettings.
    /// Pure crypto — no database writes.
    /// </summary>
    public async ValueTask<AdminSeedData> CreateAdminSeedAsync(
        DualKeyPairFull adminKeys,
        string adminUsername = "Admin",
        string adminEmail = "admin@localhost",
        string deviceName = "Admin Device")
    {
        var now = DateTime.UtcNow;
        var adminContactId = Guid.NewGuid();
        var shareGroupId = Guid.NewGuid();
        var adminSelfGroupId = Guid.NewGuid();
        var adminSelfGroupContext = BuildSelfGroupContext(adminContactId);

        var adminPrivateKeyBytes = Convert.FromBase64String(adminKeys.X25519PrivateKey);

        try
        {
            // Pass 1 — system group (admin is sole member, ECDH counterparty = self).
            var systemBundle = await CreateBundleOrThrowAsync(
                adminPrivateKeyBytes, adminKeys.X25519PublicKey, SystemGroupContext);
            var adminSystemWrappedKey = systemBundle.MemberKeys[0];

            // Pass 2 — admin's self-group (one member, the admin themselves).
            // Wrapped via HKDF(ECDH(adminPriv, adminPub), info=selfContext) —
            // a key only the holder of adminPriv can ever re-derive.
            var selfBundle = await CreateBundleOrThrowAsync(
                adminPrivateKeyBytes, adminKeys.X25519PublicKey, adminSelfGroupContext);
            var adminSelfWrappedKey = selfBundle.MemberKeys[0];

            // Sign ShareTarget credentials with admin's Ed25519 key.
            var adminEd25519Priv = Convert.FromBase64String(adminKeys.Ed25519PrivateKey);
            byte[] systemTargetSig;
            byte[] selfTargetSig;
            try
            {
                systemTargetSig = await signer.SignShareTargetAsync(
                    adminEd25519Priv, adminKeys.X25519PublicKey, SyncRole.OWNER,
                    SystemGroupContext, systemBundle.KeyVersion);
                selfTargetSig = await signer.SignShareTargetAsync(
                    adminEd25519Priv, adminKeys.X25519PublicKey, SyncRole.OWNER,
                    adminSelfGroupContext, selfBundle.KeyVersion);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(adminEd25519Priv);
            }

            return new AdminSeedData
            {
                AdminContact = new TrustedContact
                {
                    Id = adminContactId,
                    Username = adminUsername,
                    Email = adminEmail,
                    X25519PublicKey = adminKeys.X25519PublicKey,
                    Ed25519PublicKey = adminKeys.Ed25519PublicKey,
                    IsAdmin = true,
                    IsTrusted = true,
                    UpdatedAt = now,
                    SharingScope = SharingScope.PUBLIC,
                    SharingId = SystemSharingId
                },
                SystemGroup = new ShareGroup
                {
                    Id = shareGroupId,
                    GroupContext = SystemGroupContext,
                    KeyVersion = systemBundle.KeyVersion,
                    GroupAdminPublicKey = adminKeys.X25519PublicKey,
                    CreatedAt = now,
                    UpdatedAt = now,
                    SharingScope = SharingScope.PUBLIC,
                    SharingId = SystemSharingId
                },
                AdminShareTarget = new ShareTarget
                {
                    Id = Guid.NewGuid(),
                    ShareGroupId = shareGroupId,
                    KeyVersion = systemBundle.KeyVersion,
                    MemberPublicKey = adminKeys.X25519PublicKey,
                    WrappedContentKey = SerializeWrappedCek(adminSystemWrappedKey.WrappedContentKey),
                    Role = SyncRole.OWNER,
                    AdminSignature = systemTargetSig,
                    GroupAdminEd25519PublicKey = adminKeys.Ed25519PublicKey,
                    GrantedByContactId = adminContactId,
                    UpdatedAt = now,
                    SharingScope = SharingScope.PUBLIC,
                    SharingId = SystemSharingId
                },
                AdminSelfGroup = new ShareGroup
                {
                    Id = adminSelfGroupId,
                    GroupContext = adminSelfGroupContext,
                    KeyVersion = selfBundle.KeyVersion,
                    GroupAdminPublicKey = adminKeys.X25519PublicKey,
                    CreatedAt = now,
                    UpdatedAt = now,
                    SharingScope = SharingScope.CLIENT,
                    SharingId = SystemSharingId
                },
                AdminSelfTarget = new ShareTarget
                {
                    Id = Guid.NewGuid(),
                    ShareGroupId = adminSelfGroupId,
                    KeyVersion = selfBundle.KeyVersion,
                    MemberPublicKey = adminKeys.X25519PublicKey,
                    WrappedContentKey = SerializeWrappedCek(adminSelfWrappedKey.WrappedContentKey),
                    Role = SyncRole.OWNER,
                    AdminSignature = selfTargetSig,
                    GroupAdminEd25519PublicKey = adminKeys.Ed25519PublicKey,
                    GrantedByContactId = adminContactId,
                    UpdatedAt = now,
                    SharingScope = SharingScope.CLIENT,
                    SharingId = SystemSharingId
                },
                Device = new DeviceSettings
                {
                    Id = Guid.NewGuid(),
                    ClientGuid = Guid.NewGuid().ToString(),
                    DeviceName = deviceName,
                    IsAdmin = true,
                    AdminContactId = adminContactId,
                    OwnContactId = adminContactId
                }
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(adminPrivateKeyBytes);
        }
    }

    private async ValueTask<GroupKeyBundle> CreateBundleOrThrowAsync(
        byte[] adminPrivateKeyBytes,
        string adminPublicKey,
        string groupContext)
    {
        var result = await groupEncryption.CreateGroupKeysAsync(
            adminPrivateKeyBytes,
            adminPublicKey,
            [adminPublicKey],
            groupContext);

        if (!result.Success)
        {
            throw new InvalidOperationException(
                $"Failed to create group keys for '{groupContext}': {result.ErrorCode}");
        }
        var bundle = result.Value
            ?? throw new InvalidOperationException(
                $"CreateGroupKeysAsync returned null bundle for '{groupContext}'");
        if (bundle.MemberKeys.Count == 0)
        {
            throw new InvalidOperationException(
                $"CreateGroupKeysAsync returned empty MemberKeys for '{groupContext}'");
        }
        return bundle;
    }

    /// <summary>
    /// Serialize a <see cref="SymmetricEncryptedData"/> to raw byte[]: <c>[nonce(12) | ciphertext]</c>.
    /// </summary>
    public static byte[] SerializeWrappedCek(SymmetricEncryptedData wrapped)
    {
        var nonce = Convert.FromBase64String(wrapped.Nonce);
        var ciphertext = Convert.FromBase64String(wrapped.Ciphertext);
        var result = new byte[nonce.Length + ciphertext.Length];
        nonce.CopyTo(result.AsSpan());
        ciphertext.CopyTo(result.AsSpan(nonce.Length));
        return result;
    }

    /// <summary>
    /// Deserialize raw byte[] back to <see cref="SymmetricEncryptedData"/>.
    /// </summary>
    public static SymmetricEncryptedData DeserializeWrappedCek(byte[] data)
    {
        if (data.Length < 12)
        {
            throw new ArgumentException("WrappedContentKey must be at least 12 bytes (nonce)");
        }
        var nonce = Convert.ToBase64String(data.AsSpan(0, 12));
        var ciphertext = Convert.ToBase64String(data.AsSpan(12));
        return new SymmetricEncryptedData(ciphertext, nonce);
    }
}
