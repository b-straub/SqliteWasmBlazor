using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
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
/// Opt-in via <c>RUN_LIVE_RELAY_TESTS=1 dotnet test --filter
/// "Category=LiveRelay"</c>. Without the environment variable the tests are
/// reported as skipped, so CI without Herd does not touch the relay directory.
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

        // Baseline whitelist: add the sender, version 1.
        await PushAsync(
            version: 1,
            ops:
            [
                WhitelistOp.Add(HashHex(_deploymentSalt, _senderPub)),
            ]);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [LiveRelayFact]
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

    [LiveRelayFact]
    public async Task WhitelistPush_AddSecondMember_BothCanPostAndPullThroughTransport()
    {
        // Add a second sender via an incremental Add op at v2. Sender from
        // the baseline (v1) stays whitelisted.
        var secondSeed = RandomNumberGenerator.GetBytes(Ed25519SeedLength);
        var secondPub = DerivePub(secondSeed);

        var result = await PushAsync(
            version: 2,
            ops:
            [
                WhitelistOp.Add(HashHex(_deploymentSalt, secondPub)),
            ]);
        Assert.Equal(2L, result.Version);
        Assert.Equal(1, result.OperationCount);

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

    [LiveRelayFact]
    public async Task WhitelistPush_ReplayVersion_ThrowsWhitelistVersionConflict()
    {
        // Baseline (v1) was pushed in InitializeAsync. Pushing v1 again must
        // surface as a typed replay-defense exception with the relay's
        // current_version.
        var ex = await Assert.ThrowsAsync<WhitelistVersionConflictException>(async () =>
            await PushAsync(
                version: 1,
                ops:
                [
                    WhitelistOp.Add(HashHex(_deploymentSalt, _senderPub)),
                ]));
        Assert.Equal(1L, ex.AttemptedVersion);
        Assert.Equal(1L, ex.CurrentVersion);
    }

    [LiveRelayFact]
    public async Task WhitelistPush_RevokeFlipsActiveSenderTo403()
    {
        // Sender is active on the baseline. Revoke at v2; subsequent POSTs
        // must hit 403 (status='revoked' is denied for POST per design §6.2).
        var result = await PushAsync(
            version: 2,
            ops:
            [
                WhitelistOp.Revoke(
                    HashHex(_deploymentSalt, _senderPub),
                    revokedAt: DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
            ]);
        Assert.Equal(2L, result.Version);

        using var http = new HttpClient();
        var transport = NewSenderTransport(http);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await transport.SendAsync([0xDE, 0xAD]));
        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
    }

    [LiveRelayFact]
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

    [LiveRelayFact]
    public async Task ThreeActors_AdminUser1User2_AllPostAndPullAllEnvelopes()
    {
        // Stage A's canonical "everyone broadcasts" coverage. Baseline (v1) has
        // user1 (== _senderPub) on the whitelist; v2 adds the system admin
        // pubkey + a fresh user2 so all three can POST. Per actor: each pulls
        // the whole queue from cursor 0 (each transport has its own
        // InMemoryReceiveCursorStore).
        var user2Seed = RandomNumberGenerator.GetBytes(Ed25519SeedLength);
        var user2Pub = DerivePub(user2Seed);

        await PushAsync(
            version: 2,
            ops:
            [
                WhitelistOp.Add(HashHex(_deploymentSalt, _adminPub)),
                WhitelistOp.Add(HashHex(_deploymentSalt, user2Pub)),
            ]);

        using var http = new HttpClient();
        var adminTransport = NewTransportFor(http, _adminSeed, _adminPub);
        var user1Transport = NewSenderTransport(http);
        var user2Transport = NewTransportFor(http, user2Seed, user2Pub);

        var adminEnv = new byte[] { 0xAA, 0x01 };
        var user1Env = new byte[] { 0xBB, 0x02 };
        var user2Env = new byte[] { 0xCC, 0x03 };
        await adminTransport.SendAsync(adminEnv);
        await user1Transport.SendAsync(user1Env);
        await user2Transport.SendAsync(user2Env);

        foreach (var transport in new[] { adminTransport, user1Transport, user2Transport })
        {
            var pulled = new List<byte[]>();
            for (var i = 0; i < 3; i++)
            {
                var bytes = await transport.TryReceiveAsync();
                Assert.NotNull(bytes);
                pulled.Add(bytes!);
            }
            Assert.Contains(adminEnv, pulled);
            Assert.Contains(user1Env, pulled);
            Assert.Contains(user2Env, pulled);
            Assert.Null(await transport.TryReceiveAsync());
        }
    }

    [LiveRelayFact]
    public async Task WhitelistPush_NonAdminSigner_Returns401()
    {
        // A "rogue admin" — valid Ed25519 keypair but NOT matching the
        // deployment's hardwired admin_pubkey_hash. Their signed v2 push must
        // hit 401 ("admin pubkey hash does not match deployment") before any
        // signature verification, surfacing as plain HttpRequestException
        // (not WhitelistVersionConflictException — that's the 409 path).
        var rogueAdminSeed = RandomNumberGenerator.GetBytes(Ed25519SeedLength);
        var rogueAdminPub = DerivePub(rogueAdminSeed);

        using var http = new HttpClient();
        var service = new WhitelistPushService(http, RelayBase, _declarationSigner);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await service.PushAsync(
                [WhitelistOp.Add(HashHex(_deploymentSalt, rogueAdminPub))],
                adminEd25519PublicKeyBase64: Convert.ToBase64String(rogueAdminPub),
                adminEd25519PrivateKey: rogueAdminSeed,
                version: 2));
        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
    }

    [LiveRelayFact]
    public async Task RevokedSender_GraceWindowExpired_GetReturns403()
    {
        // Tighten the grace window, then revoke with a backdated revoked_at
        // beyond it. The relay's check is `now - revoked_at >= grace`, so
        // revoked_at = now - 200 with grace=60 lands in the post-grace zone
        // without sleeping — no test-clock machinery needed.
        var adminHashHex = HashHex(_deploymentSalt, _adminPub);
        WriteRelayConfig(_deploymentSalt, adminHashHex, readGraceSeconds: 60);

        var revokedAtPastGrace = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 200;
        await PushAsync(
            version: 2,
            ops:
            [
                WhitelistOp.Revoke(
                    HashHex(_deploymentSalt, _senderPub),
                    revokedAt: revokedAtPastGrace),
            ]);

        using var http = new HttpClient();
        var transport = NewSenderTransport(http);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await transport.TryReceiveAsync());
        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
    }

    [LiveRelayFact]
    public async Task RevokedSender_WithinGraceWindow_GetStillSucceeds()
    {
        // Friendly handoff: a member who's been revoked seconds ago can still
        // drain the inbox while the grace window holds — "you've been
        // revoked, here's everything that arrived before you were kicked".
        // Use revoked_at = now (just-revoked) + grace = 60 → comfortably inside.
        var adminHashHex = HashHex(_deploymentSalt, _adminPub);
        WriteRelayConfig(_deploymentSalt, adminHashHex, readGraceSeconds: 60);

        // POST one envelope while still active so the post-revoke GET has
        // something to drain.
        using var http = new HttpClient();
        var transport = NewSenderTransport(http);
        await transport.SendAsync([0x42]);

        await PushAsync(
            version: 2,
            ops:
            [
                WhitelistOp.Revoke(
                    HashHex(_deploymentSalt, _senderPub),
                    revokedAt: DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
            ]);

        var pulled = await transport.TryReceiveAsync();
        Assert.NotNull(pulled);
        Assert.Equal(new byte[] { 0x42 }, pulled!);
    }

    [LiveRelayFact]
    public async Task PostEnvelope_ExceedsBodyCap_Returns413()
    {
        // C-2 audit fix verified end-to-end: relay enforces max_body_bytes
        // before any signature work and returns 413. Keep the cap small so
        // the offending body stays small too — no megabyte uploads in tests.
        var adminHashHex = HashHex(_deploymentSalt, _adminPub);
        WriteRelayConfig(_deploymentSalt, adminHashHex, maxBodyBytes: 4096);

        using var http = new HttpClient();
        var transport = NewSenderTransport(http);

        var bigEnvelope = RandomNumberGenerator.GetBytes(8000);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await transport.SendAsync(bigEnvelope));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, ex.StatusCode);
    }

    [LiveRelayFact]
    public async Task PostEnvelope_StaleTimestamp_Returns401()
    {
        // RECEIVE_WINDOW_SECONDS = 300 in the relay, so 600s in the past is
        // safely outside. Hand-craft the POST instead of going through
        // HttpSyncTransport — the transport always uses `now`, which is the
        // contract; this test pokes at the relay's window directly.
        using var http = new HttpClient();

        var staleTs = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 600)
            .ToString(CultureInfo.InvariantCulture);
        var envelope = new byte[] { 0x77 };
        var envelopeHashHex = Convert.ToHexString(SHA256.HashData(envelope))
            .ToLowerInvariant();
        var signingInput = $"deltapost-v1|{staleTs}|{envelopeHashHex}";
        var sig = Convert.ToBase64String(
            SignEd25519(_senderSeed, Encoding.UTF8.GetBytes(signingInput)));

        using var request = new HttpRequestMessage(
            HttpMethod.Post, new Uri(RelayBase, "api/delta"))
        {
            Content = JsonContent.Create(new
            {
                envelope = Convert.ToBase64String(envelope),
            }),
        };
        request.Headers.Add("X-Timestamp", staleTs);
        request.Headers.Add("X-Sender-PubKey", Convert.ToBase64String(_senderPub));
        request.Headers.Add("X-Sender-Sig", sig);

        using var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [LiveRelayFact]
    public async Task SeedReseed_GcSignalDrivesAdminCompaction_OperationalRoundTrip()
    {
        // Operational lifecycle for the admin-only-purge model:
        //
        //   1. Admin publishes the initial seed S1 (pin POST). Relay's
        //      `deltas` holds one row, pinned=1.
        //   2. Patches accumulate via normal sender POSTs.
        //   3. Once unpinned count crosses gc_threshold_rows, every GET
        //      response includes "gc_requested": true. Non-admin clients
        //      observe the flag for diagnostics; they cannot act on it.
        //      Crucially they CANNOT trigger a purge — that would let any
        //      whitelisted client censor patches by posting a truncated
        //      rollup. Pin authority gates the only delete path.
        //   4. The deployment admin sees the flag, derives a compacted
        //      seed S2 locally, publishes via the same pin POST. The
        //      relay atomically purges S1 + every accumulated patch in a
        //      single transaction and stores S2 with pinned=1.
        //   5. Post-reseed, the unpinned count is 0; gc_requested goes
        //      back to false on subsequent GETs.
        //   6. A fresh receiver bootstrapping after the reseed sees only
        //      S2 — S1 and the accumulated patches are unrecoverable
        //      from the relay (lossy by design; receivers offline >
        //      reseed-cycle re-bootstrap from the new seed).
        //
        // Threshold tuned low (3) so the test crosses it deterministically
        // without flooding the relay. Total wall time ≈ a couple of
        // seconds — no real-time waits required.
        var adminHashHex = HashHex(_deploymentSalt, _adminPub);
        const int gcThreshold = 3;
        WriteRelayConfig(_deploymentSalt, adminHashHex, gcThresholdRows: gcThreshold);

        // Add admin to the whitelist (baseline v1 has only the sender).
        await PushAsync(
            version: 2,
            ops: [WhitelistOp.Add(HashHex(_deploymentSalt, _adminPub))]);

        using var http = new HttpClient();
        var senderTransport = NewSenderTransport(http);
        var pinService = new AdminPinService(http, RelayBase, _declarationSigner);
        var adminSenderSigner = new BcEd25519SenderSigner(_adminSeed, _adminPub);

        // ---- 1. Admin seeds S1 -------------------------------------------
        var seedS1 = new byte[] { 0x5E, 0xED, 0x01 };
        var pinS1 = await pinService.PinAsync(
            seedS1,
            adminEd25519PublicKeyBase64: Convert.ToBase64String(_adminPub),
            adminEd25519PrivateKey: _adminSeed,
            senderSigner: adminSenderSigner);
        Assert.Equal(0, pinS1.PriorRowsPurged); // empty deployment

        // First GET right after seeding: only the pinned row exists,
        // unpinned count is 0, gc_requested must be false.
        var firstReceived = await senderTransport.TryReceiveAsync();
        Assert.NotNull(firstReceived);
        Assert.Equal(seedS1, firstReceived!);
        Assert.Null(await senderTransport.TryReceiveAsync());
        Assert.False(senderTransport.LastReceiveSignalledGcRequested,
            "Right after seeding, only the pinned row exists; gc_requested must be false.");

        // ---- 2. Patches accumulate ---------------------------------------
        // Push 4 patches (one above threshold=3) so the relay's GET path
        // crosses the gc_requested boundary.
        var patches = new byte[][]
        {
            [0xA1, 0x01], [0xA2, 0x02], [0xA3, 0x03], [0xA4, 0x04],
        };
        foreach (var p in patches)
        {
            await senderTransport.SendAsync(p);
        }

        // ---- 3. GET response now signals gc_requested --------------------
        // Use a fresh transport (admin identity, cursor=0) so the puller
        // pulls everything in one go and observes the flag explicitly.
        using var observerHttp = new HttpClient();
        var observerTransport = NewTransportFor(observerHttp, _adminSeed, _adminPub);
        var pulled = new List<byte[]>();
        while (await observerTransport.TryReceiveAsync() is { } bytes)
        {
            pulled.Add(bytes);
        }
        Assert.Equal(5, pulled.Count); // S1 + 4 patches
        Assert.Equal(seedS1, pulled[0]);
        Assert.True(observerTransport.LastReceiveSignalledGcRequested,
            $"After {patches.Length} unpinned rows past threshold={gcThreshold}, "
            + "GET must set gc_requested=true.");

        // ---- 4. Admin responds: reseed with S2 ---------------------------
        var seedS2 = new byte[] { 0x5E, 0xED, 0x02 };
        var pinS2 = await pinService.PinAsync(
            seedS2,
            adminEd25519PublicKeyBase64: Convert.ToBase64String(_adminPub),
            adminEd25519PrivateKey: _adminSeed,
            senderSigner: adminSenderSigner);
        // S1 + 4 patches all purged atomically; AUTOINCREMENT preserves
        // monotonic cursor numbering across the sweep.
        Assert.Equal(5, pinS2.PriorRowsPurged);
        Assert.True(pinS2.Cursor > pinS1.Cursor,
            $"S2 cursor {pinS2.Cursor} must exceed S1 cursor {pinS1.Cursor}; AUTOINCREMENT must survive reseed purge.");

        // ---- 5. Post-reseed: gc_requested cleared on next GET -----------
        using var postReseedObserverHttp = new HttpClient();
        var postReseedObserver = NewTransportFor(postReseedObserverHttp, _adminSeed, _adminPub);
        var postReseedPulled = await postReseedObserver.TryReceiveAsync();
        Assert.NotNull(postReseedPulled);
        Assert.Equal(seedS2, postReseedPulled!);
        Assert.Null(await postReseedObserver.TryReceiveAsync());
        Assert.False(postReseedObserver.LastReceiveSignalledGcRequested,
            "After reseed, only the new pinned row exists; gc_requested must be false.");

        // ---- 6. Fresh client bootstraps off S2 alone --------------------
        // Critical: S1 + accumulated patches are unrecoverable. Lossy by
        // design — receivers offline through a reseed re-bootstrap from
        // the new seed and lose any intermediate state.
        using var bootstrapHttp = new HttpClient();
        var bootstrap = NewSenderTransport(bootstrapHttp);
        var bootstrapped = await bootstrap.TryReceiveAsync();
        Assert.NotNull(bootstrapped);
        Assert.Equal(seedS2, bootstrapped!);
        Assert.Null(await bootstrap.TryReceiveAsync());

        // Whitelist invariants: untouched across the lifecycle.
        var (whitelistRows, currentVersion) = ReadWhitelistState();
        Assert.Equal(2L, currentVersion);
        Assert.Equal(2, whitelistRows.Count);
        Assert.Contains(whitelistRows, r =>
            r.PubkeyHash == HashHex(_deploymentSalt, _senderPub) && r.Status == "active");
        Assert.Contains(whitelistRows, r =>
            r.PubkeyHash == HashHex(_deploymentSalt, _adminPub) && r.Status == "active");
    }

    [LiveRelayFact]
    public async Task Pin_NonAdminSender_Returns403()
    {
        // A regular whitelisted sender (not the deployment admin) attaches
        // a valid X-Admin-Pin-Sig (over their own keypair). The relay must
        // reject before even verifying the pin sig — pin authority is
        // gated to the deployment admin_pubkey_hash.
        using var http = new HttpClient();

        var envelope = new byte[] { 0x4F, 0x4F };
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var timestampStr = timestamp.ToString(CultureInfo.InvariantCulture);
        var envelopeHashHex = Convert
            .ToHexString(SHA256.HashData(envelope)).ToLowerInvariant();
        var senderSig = Convert.ToBase64String(SignEd25519(
            _senderSeed, Encoding.UTF8.GetBytes($"deltapost-v1|{timestampStr}|{envelopeHashHex}")));
        var pinSig = Convert.ToBase64String(SignEd25519(
            _senderSeed, Encoding.UTF8.GetBytes($"deltapin-v1|{timestampStr}|{envelopeHashHex}")));

        using var request = new HttpRequestMessage(
            HttpMethod.Post, new Uri(RelayBase, "api/delta"))
        {
            Content = JsonContent.Create(new { envelope = Convert.ToBase64String(envelope) }),
        };
        request.Headers.Add("X-Timestamp", timestampStr);
        request.Headers.Add("X-Sender-PubKey", Convert.ToBase64String(_senderPub));
        request.Headers.Add("X-Sender-Sig", senderSig);
        request.Headers.Add("X-Admin-Pin-Sig", pinSig);

        using var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
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

    private async Task<WhitelistPushResult> PushAsync(long version, IReadOnlyList<WhitelistOp> ops)
    {
        using var http = new HttpClient();
        var service = new WhitelistPushService(http, RelayBase, _declarationSigner);
        return await service.PushAsync(
            ops,
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

    private void WriteRelayConfig(
        byte[] salt,
        string adminHashHex,
        int readGraceSeconds = 604800,
        int maxBodyBytes = 1048576,
        int gcThresholdRows = 1000)
    {
        var saltB64 = Convert.ToBase64String(salt);
        var contents = $$"""
            <?php
            // Generated by HttpSyncTransportLiveRelayTests. Do not commit.
            return [
                'deployment_salt'    => '{{saltB64}}',
                'admin_pubkey_hash'  => '{{adminHashHex}}',
                'read_grace_seconds' => {{readGraceSeconds}},
                'max_body_bytes'     => {{maxBodyBytes}},
                'rate_limit_window'  => 60,
                'rate_limit_count'   => 60,
                'gc_threshold_rows'  => {{gcThresholdRows}},
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

    private record WhitelistRow(string PubkeyHash, string Status, long? RevokedAt);

    private (IReadOnlyList<WhitelistRow> Rows, long CurrentVersion) ReadWhitelistState()
    {
        var path = Path.Combine(_deltaRelayDir, "relay.db");
        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();

        var rows = new List<WhitelistRow>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT pubkey_hash, status, revoked_at FROM whitelist ORDER BY pubkey_hash";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new WhitelistRow(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetInt64(2)));
            }
        }

        long version;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT current_version FROM whitelist_meta WHERE id = 1";
            version = (long)(cmd.ExecuteScalar() ?? 0L);
        }

        return (rows, version);
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

internal sealed class LiveRelayFactAttribute : FactAttribute
{
    public LiveRelayFactAttribute()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("RUN_LIVE_RELAY_TESTS"),
                "1",
                StringComparison.Ordinal))
        {
            Skip = "Set RUN_LIVE_RELAY_TESTS=1 and link http://delta-relay.test/ to run LiveRelay tests.";
        }
    }
}
