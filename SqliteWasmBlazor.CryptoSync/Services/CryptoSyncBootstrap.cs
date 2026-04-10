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
public class CryptoSyncBootstrap(IGroupEncryption groupEncryption)
{
    /// <summary>Well-known group context for the system scope (v1).</summary>
    public const string SystemGroupContext = "system:v1";

    /// <summary>Well-known SharingId for the system scope.</summary>
    public const string SystemSharingId = "system";

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

        var adminPrivateKeyBytes = Convert.FromBase64String(adminKeys.X25519PrivateKey);

        try
        {
            var createResult = await groupEncryption.CreateGroupKeysAsync(
                adminPrivateKeyBytes,
                adminKeys.X25519PublicKey,
                [adminKeys.X25519PublicKey],
                SystemGroupContext);

            if (!createResult.Success)
            {
                throw new InvalidOperationException(
                    $"Failed to create system group keys: {createResult.ErrorCode}");
            }

            var bundle = createResult.Value
                ?? throw new InvalidOperationException("CreateGroupKeysAsync returned null bundle");

            if (bundle.MemberKeys.Count == 0)
            {
                throw new InvalidOperationException("CreateGroupKeysAsync returned empty MemberKeys");
            }

            var adminWrappedKey = bundle.MemberKeys[0];

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
                    SharingScope = SharingScope.Public,
                    SharingId = SystemSharingId
                },
                SystemGroup = new ShareGroup
                {
                    Id = shareGroupId,
                    GroupContext = SystemGroupContext,
                    KeyVersion = bundle.KeyVersion,
                    AdminPublicKey = adminKeys.X25519PublicKey,
                    CreatedAt = now,
                    UpdatedAt = now,
                    SharingScope = SharingScope.Public,
                    SharingId = SystemSharingId
                },
                AdminShareTarget = new ShareTarget
                {
                    Id = Guid.NewGuid(),
                    ShareGroupId = shareGroupId,
                    KeyVersion = bundle.KeyVersion,
                    MemberPublicKey = adminKeys.X25519PublicKey,
                    WrappedContentKey = SerializeWrappedCek(adminWrappedKey.WrappedContentKey),
                    Role = SyncRole.Owner,
                    GrantedByContactId = adminContactId,
                    UpdatedAt = now,
                    SharingScope = SharingScope.Public,
                    SharingId = SystemSharingId
                },
                Device = new DeviceSettings
                {
                    Id = Guid.NewGuid(),
                    ClientGuid = Guid.NewGuid().ToString(),
                    DeviceName = deviceName,
                    IsAdmin = true,
                    AdminContactId = adminContactId
                }
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(adminPrivateKeyBytes);
        }
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
