using SqliteWasmBlazor.Crypto.BouncyCastle;
using Xunit;

namespace SqliteWasmBlazor.CryptoSync.Tests;

public class DeclarationSignerTests
{
    private readonly BouncyCastleCryptoProvider _crypto = new();
    private readonly DeclarationSigner _signer;

    public DeclarationSignerTests()
    {
        _signer = new DeclarationSigner(_crypto);
    }

    private async Task<(string Ed25519Pub, string Ed25519Priv, byte[] Ed25519PrivBytes)> GenerateKeysAsync(byte seedByte)
    {
        var seed = new byte[32];
        for (var i = 0; i < seed.Length; i++)
        {
            seed[i] = (byte)(seedByte + i);
        }
        var keys = await _crypto.DeriveDualKeyPairAsync(seed);
        return (keys.Ed25519PublicKey, keys.Ed25519PrivateKey, Convert.FromBase64String(keys.Ed25519PrivateKey));
    }

    // ----------------------------------------------------------------
    // ShareTarget credential
    // ----------------------------------------------------------------

    [Fact]
    public async Task SignShareTarget_RoundTrip_Verifies()
    {
        var (pub, _, priv) = await GenerateKeysAsync(1);

        var sig = await _signer.SignShareTargetAsync(priv, "memberKey123", SyncRole.EDITOR, "shopping:v1", 1);

        var ok = await _signer.VerifyShareTargetAsync(pub, "memberKey123", SyncRole.EDITOR, "shopping:v1", 1, sig);
        Assert.True(ok);
    }

    [Fact]
    public async Task SignShareTarget_TamperedRole_Fails()
    {
        var (pub, _, priv) = await GenerateKeysAsync(2);

        var sig = await _signer.SignShareTargetAsync(priv, "memberKey", SyncRole.EDITOR, "group:v1", 1);

        var ok = await _signer.VerifyShareTargetAsync(pub, "memberKey", SyncRole.OWNER, "group:v1", 1, sig);
        Assert.False(ok);
    }

    [Fact]
    public async Task SignShareTarget_TamperedMemberKey_Fails()
    {
        var (pub, _, priv) = await GenerateKeysAsync(3);

        var sig = await _signer.SignShareTargetAsync(priv, "originalKey", SyncRole.VIEWER, "group:v1", 1);

        var ok = await _signer.VerifyShareTargetAsync(pub, "elevatedKey", SyncRole.VIEWER, "group:v1", 1, sig);
        Assert.False(ok);
    }

    [Fact]
    public async Task SignShareTarget_WrongSigner_Fails()
    {
        var (_, _, privA) = await GenerateKeysAsync(4);
        var (pubB, _, _) = await GenerateKeysAsync(5);

        var sig = await _signer.SignShareTargetAsync(privA, "member", SyncRole.EDITOR, "group:v1", 1);

        var ok = await _signer.VerifyShareTargetAsync(pubB, "member", SyncRole.EDITOR, "group:v1", 1, sig);
        Assert.False(ok);
    }

    // ----------------------------------------------------------------
    // Leave declaration
    // ----------------------------------------------------------------

    [Fact]
    public async Task SignLeaveDeclaration_RoundTrip_Verifies()
    {
        var (pub, _, priv) = await GenerateKeysAsync(10);

        var sig = await _signer.SignLeaveDeclarationAsync(priv, "shopping:v1", 1);

        var ok = await _signer.VerifyLeaveDeclarationAsync(pub, "shopping:v1", 1, sig);
        Assert.True(ok);
    }

    [Fact]
    public async Task SignLeaveDeclaration_TamperedGroupContext_Fails()
    {
        var (pub, _, priv) = await GenerateKeysAsync(11);

        var sig = await _signer.SignLeaveDeclarationAsync(priv, "shopping:v1", 1);

        var ok = await _signer.VerifyLeaveDeclarationAsync(pub, "other-group:v1", 1, sig);
        Assert.False(ok);
    }

    // ----------------------------------------------------------------
    // Transfer declaration
    // ----------------------------------------------------------------

    [Fact]
    public async Task SignTransferDeclaration_RoundTrip_Verifies()
    {
        var (pub, _, priv) = await GenerateKeysAsync(20);

        var sig = await _signer.SignTransferDeclarationAsync(priv, "shopping:v1", "newAdminPub123");

        var ok = await _signer.VerifyTransferDeclarationAsync(pub, "shopping:v1", "newAdminPub123", sig);
        Assert.True(ok);
    }

    [Fact]
    public async Task SignTransferDeclaration_TamperedNewAdmin_Fails()
    {
        var (pub, _, priv) = await GenerateKeysAsync(21);

        var sig = await _signer.SignTransferDeclarationAsync(priv, "shopping:v1", "legitimateAdmin");

        var ok = await _signer.VerifyTransferDeclarationAsync(pub, "shopping:v1", "attackerKey", sig);
        Assert.False(ok);
    }

    // ----------------------------------------------------------------
    // Revocation declaration
    // ----------------------------------------------------------------

    [Fact]
    public async Task SignRevocationDeclaration_RoundTrip_Verifies()
    {
        var (pub, _, priv) = await GenerateKeysAsync(30);
        var timestamp = new DateTimeOffset(2026, 4, 12, 10, 30, 0, TimeSpan.Zero);

        var sig = await _signer.SignRevocationDeclarationAsync(priv, "revokedContactPub", timestamp);

        var ok = await _signer.VerifyRevocationDeclarationAsync(pub, "revokedContactPub", timestamp, sig);
        Assert.True(ok);
    }

    [Fact]
    public async Task SignRevocationDeclaration_ReplayedWithDifferentTimestamp_Fails()
    {
        var (pub, _, priv) = await GenerateKeysAsync(31);
        var timestamp = new DateTimeOffset(2026, 4, 12, 10, 30, 0, TimeSpan.Zero);

        var sig = await _signer.SignRevocationDeclarationAsync(priv, "revokedContactPub", timestamp);

        var replayTimestamp = timestamp.AddSeconds(1);
        var ok = await _signer.VerifyRevocationDeclarationAsync(pub, "revokedContactPub", replayTimestamp, sig);
        Assert.False(ok);
    }

    // ----------------------------------------------------------------
    // Admin override transfer
    // ----------------------------------------------------------------

    [Fact]
    public async Task SignAdminOverrideTransfer_RoundTrip_Verifies()
    {
        var (pub, _, priv) = await GenerateKeysAsync(40);

        var sig = await _signer.SignAdminOverrideTransferAsync(priv, "group:v1", "revokedAdmin", "newAdmin");

        var ok = await _signer.VerifyAdminOverrideTransferAsync(pub, "group:v1", "revokedAdmin", "newAdmin", sig);
        Assert.True(ok);
    }

    [Fact]
    public async Task SignAdminOverrideTransfer_TamperedNewAdmin_Fails()
    {
        var (pub, _, priv) = await GenerateKeysAsync(41);

        var sig = await _signer.SignAdminOverrideTransferAsync(priv, "group:v1", "revokedAdmin", "legitimateNew");

        var ok = await _signer.VerifyAdminOverrideTransferAsync(pub, "group:v1", "revokedAdmin", "attackerNew", sig);
        Assert.False(ok);
    }
}
