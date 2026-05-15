using MessagePack;
using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Crypto.BouncyCastle;
using SqliteWasmBlazor.CryptoSync.Tests.Fixtures;
using Xunit;

namespace SqliteWasmBlazor.CryptoSync.Tests;

/// <summary>
/// Tests for <see cref="SyncEngine"/> — the wiring between
/// <see cref="SyncOrchestrator"/> and <see cref="ISyncTransport"/>. Crypto
/// round-trip is faked via <see cref="FakeDatabaseService"/>; these tests
/// verify cursor advance, empty-envelope skip, broadcast send, and inbox
/// drain. The actual encrypted pipeline is exercised in browser-side
/// CryptoSyncRoundTripTest.
/// </summary>
public class SyncEngineTests : IAsyncLifetime
{
    private TwoActorBootstrap _scenario = null!;

    public async Task InitializeAsync()
    {
        _scenario = await TwoActorBootstrap.CreateAsync();
    }

    public async Task DisposeAsync()
    {
        await _scenario.DisposeAsync();
    }

    [Fact]
    public async Task PushChanges_NoChanges_DoesNotSendEnvelope()
    {
        var relay = new InMemorySyncRelay();
        var transport = new InMemorySyncTransport(relay);
        var fakeDb = new FakeDatabaseService
        {
            CannedExportBytes = MessagePackSerializer.Serialize(new DeltaEnvelope
            {
                SenderEd25519PublicKey = _scenario.Admin.Keys.Ed25519PublicKey,
                Groups = []
            })
        };
        var engine = new SyncEngine(
            _scenario.Admin.Context, fakeDb, transport,
            NullImportNotifier.Instance, "admin.db");

        var sent = await engine.PushChangesAsync(_scenario.Admin.Keys);

        Assert.False(sent);
        Assert.Equal(0, relay.PendingCount);
    }

    [Fact]
    public async Task PushChanges_WithRows_BroadcastsEnvelope()
    {
        var relay = new InMemorySyncRelay();
        var adminTransport = new InMemorySyncTransport(relay);
        var userTransport = new InMemorySyncTransport(relay);

        var nonEmpty = MessagePackSerializer.Serialize(new DeltaEnvelope
        {
            SenderEd25519PublicKey = _scenario.Admin.Keys.Ed25519PublicKey,
            Groups =
            [
                new ShadowRowGroup
                {
                    TableName = "TestItems",
                    IsSystemTable = false,
                    Rows = [new ShadowRow { Id = Guid.NewGuid() }]
                }
            ]
        });
        var fakeDb = new FakeDatabaseService { CannedExportBytes = nonEmpty };

        var engine = new SyncEngine(
            _scenario.Admin.Context, fakeDb, adminTransport,
            NullImportNotifier.Instance, "admin.db");

        var sent = await engine.PushChangesAsync(_scenario.Admin.Keys);

        Assert.True(sent);
        // Broadcast: any reader can drain the queue. The user picks up the
        // envelope; the receiver's crypto layer is what filters payload
        // addressing in real life.
        var received = await userTransport.TryReceiveAsync();
        Assert.NotNull(received);
        Assert.Equal(0, relay.PendingCount);
    }

    [Fact]
    public async Task PushChanges_AdvancesCursor()
    {
        var relay = new InMemorySyncRelay();
        var transport = new InMemorySyncTransport(relay);
        var nonEmpty = MessagePackSerializer.Serialize(new DeltaEnvelope
        {
            SenderEd25519PublicKey = _scenario.Admin.Keys.Ed25519PublicKey,
            Groups =
            [
                new ShadowRowGroup
                {
                    TableName = "TestItems",
                    IsSystemTable = false,
                    Rows = [new ShadowRow { Id = Guid.NewGuid() }]
                }
            ]
        });
        var fakeDb = new FakeDatabaseService { CannedExportBytes = nonEmpty };
        var engine = new SyncEngine(
            _scenario.Admin.Context, fakeDb, transport,
            NullImportNotifier.Instance, "admin.db");

        Assert.Equal(default, await engine.GetLastExportedAtAsync());
        var before = DateTime.UtcNow;
        await engine.PushChangesAsync(_scenario.Admin.Keys);
        var after = DateTime.UtcNow;

        Assert.InRange(await engine.GetLastExportedAtAsync(), before.AddSeconds(-1), after.AddSeconds(1));
    }

    [Fact]
    public async Task PushChanges_CursorPersistsAcrossEngineInstances()
    {
        var relay = new InMemorySyncRelay();
        var transport = new InMemorySyncTransport(relay);
        var nonEmpty = MessagePackSerializer.Serialize(new DeltaEnvelope
        {
            SenderEd25519PublicKey = _scenario.Admin.Keys.Ed25519PublicKey,
            Groups =
            [
                new ShadowRowGroup
                {
                    TableName = "TestItems",
                    IsSystemTable = false,
                    Rows = [new ShadowRow { Id = Guid.NewGuid() }]
                }
            ]
        });
        var fakeDb = new FakeDatabaseService { CannedExportBytes = nonEmpty };

        var engineA = new SyncEngine(
            _scenario.Admin.Context, fakeDb, transport,
            NullImportNotifier.Instance, "admin.db");
        await engineA.PushChangesAsync(_scenario.Admin.Keys);
        var savedCursor = await engineA.GetLastExportedAtAsync();
        Assert.NotEqual(default, savedCursor);

        // Fresh engine over the same DB picks up the persisted cursor.
        var engineB = new SyncEngine(
            _scenario.Admin.Context, fakeDb, transport,
            NullImportNotifier.Instance, "admin.db");
        Assert.Equal(savedCursor, await engineB.GetLastExportedAtAsync());
    }

    [Fact]
    public async Task PullChanges_DrainsAllEnvelopes_AndAccumulatesRows()
    {
        var relay = new InMemorySyncRelay();
        var adminTransport = new InMemorySyncTransport(relay);
        var userTransport = new InMemorySyncTransport(relay);

        // Admin posts two envelopes; user drains them via broadcast.
        await adminTransport.SendAsync([0x01]);
        await adminTransport.SendAsync([0x02]);

        var fakeDb = new FakeDatabaseService
        {
            CannedImportReport = new ImportReport { RowsImported = 5 }
        };
        var notifier = new RecordingImportNotifier();
        var engine = new SyncEngine(
            _scenario.User.Context, fakeDb, userTransport,
            notifier, "user.db");

        var totalApplied = await engine.PullChangesAsync(_scenario.User.Keys);

        Assert.Equal(10, totalApplied); // 5 + 5
        Assert.Equal(2, notifier.Reports.Count);
        Assert.Null(await userTransport.TryReceiveAsync());
    }

    [Fact]
    public async Task PullChanges_EmptyInbox_ReturnsZero()
    {
        var relay = new InMemorySyncRelay();
        var transport = new InMemorySyncTransport(relay);
        var fakeDb = new FakeDatabaseService();
        var engine = new SyncEngine(
            _scenario.User.Context, fakeDb, transport,
            NullImportNotifier.Instance, "user.db");

        var applied = await engine.PullChangesAsync(_scenario.User.Keys);

        Assert.Equal(0, applied);
    }

    [Fact]
    public async Task SyncOnce_PushesThenPulls()
    {
        var relay = new InMemorySyncRelay();
        var adminTransport = new InMemorySyncTransport(relay);
        var userTransport = new InMemorySyncTransport(relay);

        // Pre-seed an envelope from the user; admin's pull will see it via
        // the broadcast queue alongside its own broadcast.
        await userTransport.SendAsync([0xFF]);

        var nonEmpty = MessagePackSerializer.Serialize(new DeltaEnvelope
        {
            SenderEd25519PublicKey = _scenario.Admin.Keys.Ed25519PublicKey,
            Groups =
            [
                new ShadowRowGroup
                {
                    TableName = "TestItems",
                    IsSystemTable = false,
                    Rows = [new ShadowRow { Id = Guid.NewGuid() }]
                }
            ]
        });
        var fakeDb = new FakeDatabaseService
        {
            CannedExportBytes = nonEmpty,
            CannedImportReport = new ImportReport { RowsImported = 7 }
        };
        var engine = new SyncEngine(
            _scenario.Admin.Context, fakeDb, adminTransport,
            NullImportNotifier.Instance, "admin.db");

        var applied = await engine.SyncOnceAsync(_scenario.Admin.Keys);

        // Push went through (envelope queued), pull drained both the
        // pre-seeded envelope and admin's own (broadcast doesn't filter at
        // the transport — receiver-side crypto would in real life).
        Assert.Equal(14, applied); // 7 per drained envelope, two envelopes
        Assert.Equal(0, relay.PendingCount);
    }

    [Fact]
    public async Task PushChanges_OneActor_StillBroadcastsAndAdvancesCursor()
    {
        // Stand up a fresh admin-only actor — broadcast model has no notion
        // of "no other contacts", so the push goes through and the cursor
        // advances regardless.
        var crypto = new BouncyCastleCryptoProvider();
        var solo = await TestActor.CreateAsync("Solo", isAdmin: true, seedByte: 1, crypto);
        try
        {
            var relay = new InMemorySyncRelay();
            var transport = new InMemorySyncTransport(relay);
            var nonEmpty = MessagePackSerializer.Serialize(new DeltaEnvelope
            {
                SenderEd25519PublicKey = solo.Keys.Ed25519PublicKey,
                Groups =
                [
                    new ShadowRowGroup
                    {
                        TableName = "TestItems",
                        IsSystemTable = false,
                        Rows = [new ShadowRow { Id = Guid.NewGuid() }]
                    }
                ]
            });
            var fakeDb = new FakeDatabaseService { CannedExportBytes = nonEmpty };
            var engine = new SyncEngine(
                solo.Context, fakeDb, transport,
                NullImportNotifier.Instance, "solo.db");

            // Bootstrap solo's DeviceSettings so BuildHeaderAsync can resolve OwnContactId.
            var seed = await solo.Bootstrap.CreateAdminSeedAsync(solo.Keys);
            await solo.Context.Database.ExecuteSqlRawAsync("DELETE FROM DeviceSettings");
            solo.Context.DeviceSettings.Add(seed.Device);
            await solo.Context.SaveChangesAsync();

            var sent = await engine.PushChangesAsync(solo.Keys);

            Assert.True(sent);
            Assert.Equal(1, relay.PendingCount);
            Assert.NotEqual(default, await engine.GetLastExportedAtAsync());
        }
        finally
        {
            await solo.DisposeAsync();
        }
    }
}
