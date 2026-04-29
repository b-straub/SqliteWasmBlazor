using System.Net;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using SqliteWasmBlazor.Crypto.Testing;
using Xunit;

namespace SqliteWasmBlazor.CryptoSync.Tests;

/// <summary>
/// Live-relay integration tests for the whitelist-broadcast wire contract.
/// Drives a Herd-served PHP relay at <c>http://delta-relay.test/</c> through
/// <see cref="HttpSyncTransport"/> + <see cref="WhitelistPushService"/>. Each
/// test resets <c>relay-config.php</c> + <c>relay.db</c> to a known seeded
/// state, so the suite is deterministic but write-heavy on the relay
/// directory.
///
/// <para>
/// Opt-in via <c>dotnet test --filter "Category=LiveRelay"</c>. The default
/// <c>dotnet test</c> run skips this category so CI without Herd doesn't
/// break.
/// </para>
///
/// <para>
/// <b>Test seeds.</b> Synthetic Ed25519 keypairs generated per-run from
/// <see cref="RandomNumberGenerator"/>, hardcoded host. No PRF, no WebAuthn —
/// per the Stage A scope in <c>~/.claude/plans/whitelist-broadcast-rewrite.md</c>.
/// Stage B replaces the seeds with PRF-backed identities; the wire contract
/// is identical so these tests stay valid.
/// </para>
/// </summary>
[Collection("LiveRelay")]
[Trait("Category", "LiveRelay")]
public sealed class HttpSyncTransportLiveRelayTests : IAsyncLifetime
{
    private static readonly Uri RelayBase = new("http://delta-relay.test/");
    private const int Ed25519SeedLength = 32;

    private string _deltaRelayDir = null!;
    private byte[] _adminSeed = null!;
    private byte[] _adminPub = null!;
    private byte[] _senderSeed = null!;
    private byte[] _senderPub = null!;
    private byte[] _deploymentSalt = null!;
    private DeclarationSigner _declarationSigner = null!;

    public async Task InitializeAsync()
    {
        _deltaRelayDir = LocateDeltaRelayDir();
        await ProbeRelayOrThrowAsync();

        _adminSeed = RandomNumberGenerator.GetBytes(Ed25519SeedLength);
        _adminPub = DerivePub(_adminSeed);
        _senderSeed = RandomNumberGenerator.GetBytes(Ed25519SeedLength);
        _senderPub = DerivePub(_senderSeed);
        _deploymentSalt = RandomNumberGenerator.GetBytes(32);

        var crypto = new BouncyCastleCryptoProvider();
        _declarationSigner = new DeclarationSigner(crypto);

        var adminHashHex = HashHex(_deploymentSalt, _adminPub);
        WriteRelayConfig(_deploymentSalt, adminHashHex);
        ResetRelayDb();

        // Baseline whitelist: just the sender, version 1.
        await PushAsync(
            version: 1,
            members:
            [
                new WhitelistMember(
                    PubkeyHash: HashHex(_deploymentSalt, _senderPub),
                    Status: WhitelistStatus.Active),
            ]);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task PostEnvelope_RoundTripsThroughLivePhpRelay()
    {
        using var http = new HttpClient();
        var transport = NewSenderTransport(http);

        var envelope = new byte[] { 0x10, 0x20, 0x30, 0x40, 0x50 };
        await transport.SendAsync(envelope);

        var received = await transport.TryReceiveAsync();
        Assert.NotNull(received);
        Assert.Equal(envelope, received!);

        Assert.Null(await transport.TryReceiveAsync());
    }

    [Fact]
    public async Task WhitelistPush_TwoActiveMembers_BothCanPostAndPullThroughTransport()
    {
        // Generate a second sender, push v2 with both members active.
        var secondSeed = RandomNumberGenerator.GetBytes(Ed25519SeedLength);
        var secondPub = DerivePub(secondSeed);

        var result = await PushAsync(
            version: 2,
            members:
            [
                new WhitelistMember(HashHex(_deploymentSalt, _senderPub), WhitelistStatus.Active),
                new WhitelistMember(HashHex(_deploymentSalt, secondPub), WhitelistStatus.Active),
            ]);
        Assert.Equal(2L, result.Version);
        Assert.Equal(2, result.MemberCount);

        using var http = new HttpClient();
        var firstTransport = NewSenderTransport(http);
        var secondTransport = NewTransportFor(http, secondSeed, secondPub);

        var envFromFirst = new byte[] { 0xA1, 0xA2 };
        var envFromSecond = new byte[] { 0xB1, 0xB2, 0xB3 };
        await firstTransport.SendAsync(envFromFirst);
        await secondTransport.SendAsync(envFromSecond);

        // Either side polls and the broadcast queue serves both envelopes.
        var pulled = new List<byte[]>();
        for (int i = 0; i < 2; i++)
        {
            var bytes = await secondTransport.TryReceiveAsync();
            Assert.NotNull(bytes);
            pulled.Add(bytes!);
        }
        Assert.Contains(envFromFirst, pulled);
        Assert.Contains(envFromSecond, pulled);
        Assert.Null(await secondTransport.TryReceiveAsync());
    }

    [Fact]
    public async Task WhitelistPush_ReplayVersion_ThrowsWhitelistVersionConflict()
    {
        // Baseline (v1) was pushed in InitializeAsync. Pushing v1 again must
        // surface as a typed replay-defense exception with the relay's
        // current_version.
        var ex = await Assert.ThrowsAsync<WhitelistVersionConflictException>(async () =>
            await PushAsync(
                version: 1,
                members:
                [
                    new WhitelistMember(HashHex(_deploymentSalt, _senderPub), WhitelistStatus.Active),
                ]));
        Assert.Equal(1L, ex.AttemptedVersion);
        Assert.Equal(1L, ex.CurrentVersion);
    }

    [Fact]
    public async Task PostEnvelope_NonWhitelistedSender_Returns403()
    {
        // Generate a third sender NOT on the whitelist.
        var rogueSeed = RandomNumberGenerator.GetBytes(Ed25519SeedLength);
        var roguePub = DerivePub(rogueSeed);

        using var http = new HttpClient();
        var transport = NewTransportFor(http, rogueSeed, roguePub);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await transport.SendAsync([0xCC]));
        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private HttpSyncTransport NewSenderTransport(HttpClient http)
        => NewTransportFor(http, _senderSeed, _senderPub);

    private static HttpSyncTransport NewTransportFor(HttpClient http, byte[] seed, byte[] pub)
    {
        var senderSigner = new BcEd25519SenderSigner(seed, pub);
        var receiveSigner = new BcEd25519ReceiveSigner(seed, pub);
        return new HttpSyncTransport(http, RelayBase, senderSigner, receiveSigner);
    }

    private async Task<WhitelistPushResult> PushAsync(long version, IReadOnlyList<WhitelistMember> members)
    {
        using var http = new HttpClient();
        var service = new WhitelistPushService(http, RelayBase, _declarationSigner);
        return await service.PushAsync(
            members,
            adminEd25519PublicKeyBase64: Convert.ToBase64String(_adminPub),
            adminEd25519PrivateKey: _adminSeed,
            version: version);
    }

    private static string LocateDeltaRelayDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "DeltaRelay", "delta-relay.php");
            if (File.Exists(candidate))
            {
                return Path.Combine(dir.FullName, "DeltaRelay");
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            "LiveRelay setup: cannot locate DeltaRelay directory by walking up from "
            + $"'{AppContext.BaseDirectory}'. Run tests from inside the SqliteWasmBlazor checkout.");
    }

    private static async Task ProbeRelayOrThrowAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        try
        {
            using var resp = await http.GetAsync(RelayBase);
            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                throw new InvalidOperationException(
                    $"LiveRelay setup: GET {RelayBase} returned 404 — Herd/Valet "
                    + "is running but the delta-relay.test site is not linked. "
                    + "Run 'herd link delta-relay' (or 'valet link delta-relay') from "
                    + "the DeltaRelay directory before running LiveRelay tests.");
            }
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException(
                $"LiveRelay setup: GET {RelayBase} failed — Herd/Valet not "
                + "running. Start it before running LiveRelay tests.", ex);
        }
    }

    private void WriteRelayConfig(byte[] salt, string adminHashHex)
    {
        var saltB64 = Convert.ToBase64String(salt);
        var contents = $$"""
            <?php
            // Generated by HttpSyncTransportLiveRelayTests. Do not commit.
            return [
                'deployment_salt'    => '{{saltB64}}',
                'admin_pubkey_hash'  => '{{adminHashHex}}',
                'read_grace_seconds' => 604800,
                'max_body_bytes'     => 1048576,
                'rate_limit_window'  => 60,
                'rate_limit_count'   => 60,
                'retention_seconds'  => 2592000,
            ];
            """;
        var path = Path.Combine(_deltaRelayDir, "relay-config.php");
        File.WriteAllText(path, contents);
    }

    private void ResetRelayDb()
    {
        TryDelete(Path.Combine(_deltaRelayDir, "relay.db"));
        TryDelete(Path.Combine(_deltaRelayDir, "relay.db-wal"));
        TryDelete(Path.Combine(_deltaRelayDir, "relay.db-shm"));
    }

    private static void TryDelete(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static string HashHex(byte[] salt, byte[] pubKey)
    {
        var buffer = new byte[salt.Length + pubKey.Length];
        Buffer.BlockCopy(salt, 0, buffer, 0, salt.Length);
        Buffer.BlockCopy(pubKey, 0, buffer, salt.Length, pubKey.Length);
        return Convert.ToHexString(SHA256.HashData(buffer)).ToLowerInvariant();
    }

    private static byte[] DerivePub(byte[] seed)
    {
        var priv = new Ed25519PrivateKeyParameters(seed, 0);
        return priv.GeneratePublicKey().GetEncoded();
    }

    private static byte[] SignEd25519(byte[] seed, byte[] message)
    {
        var signer = new Ed25519Signer();
        signer.Init(forSigning: true, new Ed25519PrivateKeyParameters(seed, 0));
        signer.BlockUpdate(message, 0, message.Length);
        return signer.GenerateSignature();
    }

    private sealed class BcEd25519SenderSigner(byte[] seed, byte[] pub) : ISenderAuthSigner
    {
        public string OwnEd25519PublicKeyBase64 { get; } = Convert.ToBase64String(pub);

        public ValueTask<string> SignSendChallengeAsync(string message, CancellationToken cancellationToken = default)
        {
            var sig = SignEd25519(seed, Encoding.UTF8.GetBytes(message));
            return ValueTask.FromResult(Convert.ToBase64String(sig));
        }
    }

    private sealed class BcEd25519ReceiveSigner(byte[] seed, byte[] pub) : IReceiveAuthSigner
    {
        public string OwnEd25519PublicKeyBase64 { get; } = Convert.ToBase64String(pub);

        public ValueTask<string> SignReceiveChallengeAsync(string message, CancellationToken cancellationToken = default)
        {
            var sig = SignEd25519(seed, Encoding.UTF8.GetBytes(message));
            return ValueTask.FromResult(Convert.ToBase64String(sig));
        }
    }
}
