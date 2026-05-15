using System.Net;
using System.Text;
using System.Text.Json;
using SqliteWasmBlazor.Crypto.BouncyCastle;
using SqliteWasmBlazor.CryptoSync.Tests.Fixtures;
using Xunit;

namespace SqliteWasmBlazor.CryptoSync.Tests;

/// <summary>
/// Wire-shape coverage for <see cref="WhitelistPushService"/> against the
/// admin op-based <c>POST /api/whitelist</c> contract documented in
/// <c>docs/security/relay-whitelist-design.md</c>. Asserts request body
/// layout, canonical-string parity (admin-order preserving) with PHP's
/// <c>buildWhitelistOpsCanonical</c>, version-replay → typed exception,
/// and success-response parsing. Real round-trip against Herd is covered in
/// <see cref="HttpSyncTransportLiveRelayTests"/>.
/// </summary>
public class WhitelistPushServiceTests
{
    private static readonly Uri RelayBase = new("http://delta-relay.test/");

    [Fact]
    public async Task PushAsync_PostsCanonicalBodyShape()
    {
        var (signer, adminPubB64, adminPriv) = await NewSignerWithKeysAsync();
        var handler = new StubHttpMessageHandler
        {
            Responder = _ => JsonOk("""{"version":3,"operation_count":2}""")
        };
        using var http = new HttpClient(handler);
        var service = new WhitelistPushService(http, RelayBase, signer);

        var ops = new WhitelistOp[]
        {
            WhitelistOp.Add(new string('a', 64)),
            WhitelistOp.Revoke(new string('b', 64), revokedAt: 1700000000),
        };

        var result = await service.PushAsync(ops, adminPubB64, adminPriv, version: 3);

        Assert.Equal(3L, result.Version);
        Assert.Equal(2, result.OperationCount);

        var req = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("http://delta-relay.test/api/whitelist", req.RequestUri.ToString());

        using var doc = JsonDocument.Parse(req.Body!);
        var root = doc.RootElement;
        Assert.Equal(3L, root.GetProperty("version").GetInt64());
        Assert.Equal(adminPubB64, root.GetProperty("admin_pubkey").GetString());
        Assert.False(string.IsNullOrEmpty(root.GetProperty("admin_signature").GetString()));

        var wireOps = root.GetProperty("operations").EnumerateArray().ToArray();
        Assert.Equal(2, wireOps.Length);
        Assert.Equal("add", wireOps[0].GetProperty("op").GetString());
        Assert.Equal(new string('a', 64), wireOps[0].GetProperty("pubkey_hash").GetString());
        Assert.False(wireOps[0].TryGetProperty("revoked_at", out _),
            "add op must not carry revoked_at");

        Assert.Equal("revoke", wireOps[1].GetProperty("op").GetString());
        Assert.Equal(new string('b', 64), wireOps[1].GetProperty("pubkey_hash").GetString());
        Assert.Equal(1700000000L, wireOps[1].GetProperty("revoked_at").GetInt64());
    }

    [Fact]
    public async Task PushAsync_AdminSignatureVerifiesAgainstCanonical()
    {
        var crypto = new BouncyCastleCryptoProvider();
        var signer = new DeclarationSigner(crypto);
        var (adminPubB64, adminPriv) = await NewAdminKeyPairAsync();
        var handler = new StubHttpMessageHandler
        {
            Responder = _ => JsonOk("""{"version":1,"operation_count":1}""")
        };
        using var http = new HttpClient(handler);
        var service = new WhitelistPushService(http, RelayBase, signer);

        var ops = new WhitelistOp[]
        {
            WhitelistOp.Add(new string('c', 64)),
        };

        await service.PushAsync(ops, adminPubB64, adminPriv, version: 1);

        using var doc = JsonDocument.Parse(handler.Requests[0].Body!);
        var sigB64 = doc.RootElement.GetProperty("admin_signature").GetString()!;

        // The signature must verify against the canonical string the service
        // built — same one the PHP relay reconstructs.
        var canonical = DeclarationSigner.BuildWhitelistOpsCanonical(version: 1, ops);
        var ok = await crypto.VerifyAsync(canonical, sigB64, adminPubB64);
        Assert.True(ok);
    }

    [Fact]
    public void BuildWhitelistOpsCanonical_PreservesAdminOrder()
    {
        // Order-significant: revoke-then-add is a different operation than
        // add-then-revoke on the same hash, so the canonical mirrors admin
        // input order. PHP's buildWhitelistOpsCanonical does the same — no
        // sort.
        var ops = new WhitelistOp[]
        {
            WhitelistOp.Revoke(new string('a', 64), revokedAt: 42),
            WhitelistOp.Add(new string('z', 64)),
            WhitelistOp.Add(new string('m', 64)),
        };

        var canonical = DeclarationSigner.BuildWhitelistOpsCanonical(version: 7, ops);

        Assert.Equal(
            "whitelist-ops-v1|7"
            + $"|revoke:{new string('a', 64)}:42"
            + $"|add:{new string('z', 64)}"
            + $"|add:{new string('m', 64)}",
            canonical);
    }

    [Fact]
    public async Task PushAsync_EmptyOperations_ThrowsArgumentException()
    {
        var (signer, adminPubB64, adminPriv) = await NewSignerWithKeysAsync();
        using var http = new HttpClient(new StubHttpMessageHandler());
        var service = new WhitelistPushService(http, RelayBase, signer);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.PushAsync(Array.Empty<WhitelistOp>(), adminPubB64, adminPriv, version: 1));
    }

    [Fact]
    public async Task PushAsync_NonPositiveVersion_ThrowsArgumentOutOfRange()
    {
        var (signer, adminPubB64, adminPriv) = await NewSignerWithKeysAsync();
        using var http = new HttpClient(new StubHttpMessageHandler());
        var service = new WhitelistPushService(http, RelayBase, signer);

        var ops = new WhitelistOp[] { WhitelistOp.Add(new string('a', 64)) };

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await service.PushAsync(ops, adminPubB64, adminPriv, version: 0));
    }

    [Fact]
    public async Task PushAsync_409Conflict_ThrowsWhitelistVersionConflictWithReportedCurrent()
    {
        var (signer, adminPubB64, adminPriv) = await NewSignerWithKeysAsync();
        var handler = new StubHttpMessageHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.Conflict)
            {
                Content = new StringContent(
                    """{"error":"version not greater than current_version","current_version":12}""",
                    Encoding.UTF8, "application/json"),
            }
        };
        using var http = new HttpClient(handler);
        var service = new WhitelistPushService(http, RelayBase, signer);

        var ops = new WhitelistOp[] { WhitelistOp.Add(new string('a', 64)) };

        var ex = await Assert.ThrowsAsync<WhitelistVersionConflictException>(async () =>
            await service.PushAsync(ops, adminPubB64, adminPriv, version: 5));
        Assert.Equal(5L, ex.AttemptedVersion);
        Assert.Equal(12L, ex.CurrentVersion);
    }

    [Fact]
    public async Task PushAsync_401Unauthorized_ThrowsHttpRequestException()
    {
        var (signer, adminPubB64, adminPriv) = await NewSignerWithKeysAsync();
        var handler = new StubHttpMessageHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent(
                    """{"error":"admin pubkey hash does not match deployment"}""",
                    Encoding.UTF8, "application/json"),
            }
        };
        using var http = new HttpClient(handler);
        var service = new WhitelistPushService(http, RelayBase, signer);

        var ops = new WhitelistOp[] { WhitelistOp.Add(new string('a', 64)) };

        await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await service.PushAsync(ops, adminPubB64, adminPriv, version: 1));
    }

    [Fact]
    public void HashPubkey_MatchesPhpFormat()
    {
        // sha256(salt || pubkey), lowercase hex. Parity-test against the
        // PHP relay's pubkeyHash() function on a known input.
        var salt = new byte[] { 0x00, 0x01, 0x02, 0x03 };
        var pubkey = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD };
        var combined = salt.Concat(pubkey).ToArray();
        var expected = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(combined)).ToLowerInvariant();

        var actual = WhitelistPushService.HashPubkey(
            Convert.ToBase64String(salt),
            Convert.ToBase64String(pubkey));

        Assert.Equal(expected, actual);
    }

    private static async Task<(DeclarationSigner Signer, string AdminPubB64, byte[] AdminPriv)> NewSignerWithKeysAsync()
    {
        var crypto = new BouncyCastleCryptoProvider();
        var signer = new DeclarationSigner(crypto);
        var (pub, priv) = await NewAdminKeyPairAsync(crypto);
        return (signer, pub, priv);
    }

    private static Task<(string Pub, byte[] Priv)> NewAdminKeyPairAsync()
        => NewAdminKeyPairAsync(new BouncyCastleCryptoProvider());

    private static async Task<(string Pub, byte[] Priv)> NewAdminKeyPairAsync(SqliteWasmBlazor.Crypto.Abstractions.ICryptoProvider crypto)
    {
        var prfSeed = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var dual = await crypto.DeriveDualKeyPairAsync(prfSeed);
        return (dual.Ed25519PublicKey, dual.Ed25519PrivateKey);
    }

    private static HttpResponseMessage JsonOk(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };
}
