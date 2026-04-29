using System.Net;
using System.Text;
using System.Text.Json;
using SqliteWasmBlazor.Crypto.Testing;
using SqliteWasmBlazor.CryptoSync.Tests.Fixtures;
using Xunit;

namespace SqliteWasmBlazor.CryptoSync.Tests;

/// <summary>
/// Wire-shape coverage for <see cref="WhitelistPushService"/> against the
/// admin <c>POST /api/whitelist</c> contract documented in
/// <c>docs/security/relay-whitelist-design.md</c> §6.1. Asserts request body
/// layout, canonical-string lex-sort parity with PHP's
/// <c>buildWhitelistSigningString</c>, version-replay → typed exception, and
/// success-response parsing. Real round-trip against Herd is covered in
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
            Responder = _ => JsonOk("""{"version":3,"member_count":2}""")
        };
        using var http = new HttpClient(handler);
        var service = new WhitelistPushService(http, RelayBase, signer);

        var members = new[]
        {
            new WhitelistMember("aa".PadRight(64, 'a'), WhitelistStatus.Active),
            new WhitelistMember("bb".PadRight(64, 'b'), WhitelistStatus.Revoked, RevokedAt: 1700000000),
        };

        var result = await service.PushAsync(members, adminPubB64, adminPriv, version: 3);

        Assert.Equal(3L, result.Version);
        Assert.Equal(2, result.MemberCount);

        var req = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("http://delta-relay.test/api/whitelist", req.RequestUri.ToString());

        using var doc = JsonDocument.Parse(req.Body!);
        var root = doc.RootElement;
        Assert.Equal(3L, root.GetProperty("version").GetInt64());
        Assert.Equal(adminPubB64, root.GetProperty("admin_pubkey").GetString());
        Assert.False(string.IsNullOrEmpty(root.GetProperty("admin_signature").GetString()));

        var wireMembers = root.GetProperty("members").EnumerateArray().ToArray();
        Assert.Equal(2, wireMembers.Length);
        var active = wireMembers.Single(m => m.GetProperty("status").GetString() == "active");
        Assert.Equal("aa".PadRight(64, 'a'), active.GetProperty("pubkey_hash").GetString());
        Assert.False(active.TryGetProperty("revoked_at", out _),
            "active members must not carry a revoked_at field on the wire");

        var revoked = wireMembers.Single(m => m.GetProperty("status").GetString() == "revoked");
        Assert.Equal("bb".PadRight(64, 'b'), revoked.GetProperty("pubkey_hash").GetString());
        Assert.Equal(1700000000L, revoked.GetProperty("revoked_at").GetInt64());
    }

    [Fact]
    public async Task PushAsync_AdminSignatureVerifiesAgainstCanonical()
    {
        var crypto = new BouncyCastleCryptoProvider();
        var signer = new DeclarationSigner(crypto);
        var (adminPubB64, adminPriv) = await NewAdminKeyPairAsync();
        var handler = new StubHttpMessageHandler
        {
            Responder = _ => JsonOk("""{"version":1,"member_count":1}""")
        };
        using var http = new HttpClient(handler);
        var service = new WhitelistPushService(http, RelayBase, signer);

        var members = new[]
        {
            new WhitelistMember(new string('c', 64), WhitelistStatus.Active),
        };

        await service.PushAsync(members, adminPubB64, adminPriv, version: 1);

        using var doc = JsonDocument.Parse(handler.Requests[0].Body!);
        var sigB64 = doc.RootElement.GetProperty("admin_signature").GetString()!;

        // The signature must verify against the canonical string the service
        // built — same one the PHP relay reconstructs.
        var canonical = DeclarationSigner.BuildWhitelistPushCanonical(version: 1, members);
        var ok = await crypto.VerifyAsync(canonical, sigB64, adminPubB64);
        Assert.True(ok);
    }

    [Fact]
    public void BuildWhitelistPushCanonical_LexSortsByRowString()
    {
        // Adversarial: out-of-order input, mixed status. Expected canonical
        // matches PHP's SORT_STRING (byte-wise lex) on
        // "{pubkey_hash}:{status}:{revoked_or_zero}".
        var members = new[]
        {
            new WhitelistMember(new string('z', 64), WhitelistStatus.Active),
            new WhitelistMember(new string('a', 64), WhitelistStatus.Revoked, RevokedAt: 42),
            new WhitelistMember(new string('m', 64), WhitelistStatus.Active),
        };

        var canonical = DeclarationSigner.BuildWhitelistPushCanonical(version: 7, members);

        var expectedRows = new[]
        {
            $"{new string('a', 64)}:revoked:42",
            $"{new string('m', 64)}:active:0",
            $"{new string('z', 64)}:active:0",
        };
        Assert.Equal($"whitelist-v1|7|{string.Join("|", expectedRows)}", canonical);
    }

    [Fact]
    public async Task PushAsync_RevokedWithoutRevokedAt_ThrowsArgumentException()
    {
        var (signer, adminPubB64, adminPriv) = await NewSignerWithKeysAsync();
        using var http = new HttpClient(new StubHttpMessageHandler());
        var service = new WhitelistPushService(http, RelayBase, signer);

        var members = new[]
        {
            new WhitelistMember(new string('a', 64), WhitelistStatus.Revoked, RevokedAt: null),
        };

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.PushAsync(members, adminPubB64, adminPriv, version: 1));
    }

    [Fact]
    public async Task PushAsync_NonPositiveVersion_ThrowsArgumentOutOfRange()
    {
        var (signer, adminPubB64, adminPriv) = await NewSignerWithKeysAsync();
        using var http = new HttpClient(new StubHttpMessageHandler());
        var service = new WhitelistPushService(http, RelayBase, signer);

        var members = new[]
        {
            new WhitelistMember(new string('a', 64), WhitelistStatus.Active),
        };

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await service.PushAsync(members, adminPubB64, adminPriv, version: 0));
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

        var members = new[]
        {
            new WhitelistMember(new string('a', 64), WhitelistStatus.Active),
        };

        var ex = await Assert.ThrowsAsync<WhitelistVersionConflictException>(async () =>
            await service.PushAsync(members, adminPubB64, adminPriv, version: 5));
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

        var members = new[]
        {
            new WhitelistMember(new string('a', 64), WhitelistStatus.Active),
        };

        await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await service.PushAsync(members, adminPubB64, adminPriv, version: 1));
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

    private static async Task<(string Pub, byte[] Priv)> NewAdminKeyPairAsync(Crypto.Abstractions.ICryptoProvider crypto)
    {
        // Per-test PRF seed; deterministic-isn't-needed since we sign + verify
        // in-process with the same crypto provider.
        var prfSeed = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var dual = await crypto.DeriveDualKeyPairAsync(prfSeed);
        return (dual.Ed25519PublicKey, Convert.FromBase64String(dual.Ed25519PrivateKey));
    }

    private static HttpResponseMessage JsonOk(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };
}
