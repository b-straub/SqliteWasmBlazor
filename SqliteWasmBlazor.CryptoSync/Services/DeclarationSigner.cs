using SqliteWasmBlazor.Crypto.Abstractions;

namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// Constructs canonical payloads and signs/verifies Ed25519 declarations
/// for the CryptoSync authorization model. All methods delegate to
/// <see cref="ICryptoProvider.SignAsync"/> / <see cref="ICryptoProvider.VerifyAsync"/>
/// — no new crypto primitives needed.
///
/// <para>
/// Canonical format: pipe-delimited UTF-8 string. Integers are decimal
/// strings. Deterministic and debuggable.
/// </para>
/// </summary>
public class DeclarationSigner(ICryptoProvider crypto)
{
    // ================================================================
    // ShareTarget credential (GroupAdmin signs)
    // ================================================================

    /// <summary>
    /// GroupAdmin signs a credential granting a member a role in a group.
    /// Canonical: <c>memberPublicKey | role | groupContext | keyVersion</c>
    /// </summary>
    public async ValueTask<byte[]> SignShareTargetAsync(
        ReadOnlyMemory<byte> groupAdminEd25519PrivateKey,
        string memberPublicKeyBase64,
        SyncRole role,
        string groupContext,
        int keyVersion)
    {
        var canonical = BuildShareTargetCanonical(memberPublicKeyBase64, role, groupContext, keyVersion);
        var result = await crypto.SignAsync(canonical, groupAdminEd25519PrivateKey);
        if (!result.Success || result.Value is null)
        {
            throw new InvalidOperationException($"DeclarationSigner: SignShareTargetAsync failed: {result.ErrorCode}");
        }
        return Convert.FromBase64String(result.Value);
    }

    /// <summary>
    /// Verify a GroupAdmin's signature on a ShareTarget credential.
    /// </summary>
    public async ValueTask<bool> VerifyShareTargetAsync(
        string groupAdminEd25519PublicKeyBase64,
        string memberPublicKeyBase64,
        SyncRole role,
        string groupContext,
        int keyVersion,
        byte[] adminSignature)
    {
        var canonical = BuildShareTargetCanonical(memberPublicKeyBase64, role, groupContext, keyVersion);
        return await crypto.VerifyAsync(canonical, Convert.ToBase64String(adminSignature), groupAdminEd25519PublicKeyBase64);
    }

    private static string BuildShareTargetCanonical(string memberPublicKeyBase64, SyncRole role, string groupContext, int keyVersion)
        => $"{memberPublicKeyBase64}|{(int)role}|{groupContext}|{keyVersion}";

    // ================================================================
    // Leave declaration (member signs)
    // ================================================================

    /// <summary>
    /// Member signs a voluntary leave declaration.
    /// Canonical: <c>groupContext | keyVersion | "leave"</c>
    /// </summary>
    public async ValueTask<byte[]> SignLeaveDeclarationAsync(
        ReadOnlyMemory<byte> memberEd25519PrivateKey,
        string groupContext,
        int keyVersion)
    {
        var canonical = $"{groupContext}|{keyVersion}|leave";
        var result = await crypto.SignAsync(canonical, memberEd25519PrivateKey);
        if (!result.Success || result.Value is null)
        {
            throw new InvalidOperationException($"DeclarationSigner: SignLeaveDeclarationAsync failed: {result.ErrorCode}");
        }
        return Convert.FromBase64String(result.Value);
    }

    /// <summary>
    /// Verify a member's leave declaration.
    /// </summary>
    public async ValueTask<bool> VerifyLeaveDeclarationAsync(
        string memberEd25519PublicKeyBase64,
        string groupContext,
        int keyVersion,
        byte[] signature)
    {
        var canonical = $"{groupContext}|{keyVersion}|leave";
        return await crypto.VerifyAsync(canonical, Convert.ToBase64String(signature), memberEd25519PublicKeyBase64);
    }

    // ================================================================
    // Transfer declaration (old GroupAdmin signs)
    // ================================================================

    /// <summary>
    /// Current GroupAdmin signs a declaration transferring ownership.
    /// Canonical: <c>groupContext | "transfer-admin" | newGroupAdminEd25519PublicKey</c>
    /// </summary>
    public async ValueTask<byte[]> SignTransferDeclarationAsync(
        ReadOnlyMemory<byte> oldGroupAdminEd25519PrivateKey,
        string groupContext,
        string newGroupAdminEd25519PublicKeyBase64)
    {
        var canonical = $"{groupContext}|transfer-admin|{newGroupAdminEd25519PublicKeyBase64}";
        var result = await crypto.SignAsync(canonical, oldGroupAdminEd25519PrivateKey);
        if (!result.Success || result.Value is null)
        {
            throw new InvalidOperationException($"DeclarationSigner: SignTransferDeclarationAsync failed: {result.ErrorCode}");
        }
        return Convert.FromBase64String(result.Value);
    }

    /// <summary>
    /// Verify a GroupAdmin transfer declaration.
    /// </summary>
    public async ValueTask<bool> VerifyTransferDeclarationAsync(
        string oldGroupAdminEd25519PublicKeyBase64,
        string groupContext,
        string newGroupAdminEd25519PublicKeyBase64,
        byte[] signature)
    {
        var canonical = $"{groupContext}|transfer-admin|{newGroupAdminEd25519PublicKeyBase64}";
        return await crypto.VerifyAsync(canonical, Convert.ToBase64String(signature), oldGroupAdminEd25519PublicKeyBase64);
    }

    // ================================================================
    // Revocation declaration (Domain Admin signs)
    // ================================================================

    /// <summary>
    /// Domain Admin signs a revocation of trust for a contact.
    /// Canonical: <c>contactEd25519PublicKey | "revoke" | timestamp(ISO 8601 UTC)</c>
    /// </summary>
    public async ValueTask<byte[]> SignRevocationDeclarationAsync(
        ReadOnlyMemory<byte> adminEd25519PrivateKey,
        string contactEd25519PublicKeyBase64,
        DateTimeOffset timestamp)
    {
        var canonical = BuildRevocationCanonical(contactEd25519PublicKeyBase64, timestamp);
        var result = await crypto.SignAsync(canonical, adminEd25519PrivateKey);
        if (!result.Success || result.Value is null)
        {
            throw new InvalidOperationException($"DeclarationSigner: SignRevocationDeclarationAsync failed: {result.ErrorCode}");
        }
        return Convert.FromBase64String(result.Value);
    }

    /// <summary>
    /// Verify a Domain Admin's revocation declaration.
    /// </summary>
    public async ValueTask<bool> VerifyRevocationDeclarationAsync(
        string adminEd25519PublicKeyBase64,
        string contactEd25519PublicKeyBase64,
        DateTimeOffset timestamp,
        byte[] adminSignature)
    {
        var canonical = BuildRevocationCanonical(contactEd25519PublicKeyBase64, timestamp);
        return await crypto.VerifyAsync(canonical, Convert.ToBase64String(adminSignature), adminEd25519PublicKeyBase64);
    }

    private static string BuildRevocationCanonical(string contactEd25519PublicKeyBase64, DateTimeOffset timestamp)
        => $"{contactEd25519PublicKeyBase64}|revoke|{timestamp.UtcDateTime:O}";

    // ================================================================
    // Admin override transfer (Domain Admin signs)
    // ================================================================

    /// <summary>
    /// Domain Admin forcibly transfers a group away from a revoked GroupAdmin.
    /// Canonical: <c>groupContext | "admin-override-transfer" | revokedGroupAdminEd25519PublicKey | newGroupAdminEd25519PublicKey</c>
    /// </summary>
    public async ValueTask<byte[]> SignAdminOverrideTransferAsync(
        ReadOnlyMemory<byte> adminEd25519PrivateKey,
        string groupContext,
        string revokedGroupAdminEd25519PublicKeyBase64,
        string newGroupAdminEd25519PublicKeyBase64)
    {
        var canonical = $"{groupContext}|admin-override-transfer|{revokedGroupAdminEd25519PublicKeyBase64}|{newGroupAdminEd25519PublicKeyBase64}";
        var result = await crypto.SignAsync(canonical, adminEd25519PrivateKey);
        if (!result.Success || result.Value is null)
        {
            throw new InvalidOperationException($"DeclarationSigner: SignAdminOverrideTransferAsync failed: {result.ErrorCode}");
        }
        return Convert.FromBase64String(result.Value);
    }

    /// <summary>
    /// Verify a Domain Admin's override transfer declaration.
    /// </summary>
    public async ValueTask<bool> VerifyAdminOverrideTransferAsync(
        string adminEd25519PublicKeyBase64,
        string groupContext,
        string revokedGroupAdminEd25519PublicKeyBase64,
        string newGroupAdminEd25519PublicKeyBase64,
        byte[] adminSignature)
    {
        var canonical = $"{groupContext}|admin-override-transfer|{revokedGroupAdminEd25519PublicKeyBase64}|{newGroupAdminEd25519PublicKeyBase64}";
        return await crypto.VerifyAsync(canonical, Convert.ToBase64String(adminSignature), adminEd25519PublicKeyBase64);
    }
}
