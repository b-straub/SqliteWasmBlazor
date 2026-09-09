using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Crypto;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.Migrations.Upgrade;

/// <summary>
/// Where a pending migration actually gets applied — the part the other
/// upgrade cases skip by calling <c>MigrateAsync</c> themselves.
/// </summary>
/// <remarks>
/// <para>
/// An encrypted pool is locked at every boot: the key comes from a WebAuthn
/// ceremony that cannot run before the app renders. So initialization has to
/// survive being driven while the pages are still ciphertext, and the unlock
/// has to be what finishes it. Get either wrong and the migration is silently
/// never applied — the consumer meets it as <c>no such column</c> at their
/// first query.
/// </para>
/// <para>
/// The locked phase also covers the multi-context invariant. This app declares
/// <c>TodoDbContext</c> before <c>MigrationProbeContext</c>, so the probe
/// reaching V2 proves the sequence ran past the first declaration rather than
/// stopping at it.
/// </para>
/// </remarks>
internal sealed class BootDeferredMigrationTest(
    IServiceProvider services,
    ISqliteWasmDatabaseService databaseService,
    IEncryptedSqliteWasmDatabaseService session)
    : MigrationUpgradeTestBase(services)
{
    private const string CredentialId = "test-credential-id-boot-deferred-migration";

    /// <summary>
    /// Enough rows to prove they survive, and no more: this case is about
    /// where the migration runs, not what it costs. The cost is
    /// <see cref="PopulatedUpgradeTest"/>'s job.
    /// </summary>
    protected override int SeedRows => 200;

    public override string Name => "Migration_BootDefersAndUnlockApplies";

    private IDbInitializationStatus Status =>
        Services.GetRequiredService<IDbInitializationStatus>();

    private ISqliteWasmInitializer Initializer =>
        Services.GetRequiredService<ISqliteWasmInitializer>();

    protected override async ValueTask PrepareAsync()
    {
        // Drop the worker-wide key. The encrypted upgrade case before this one
        // installs one without a manifest, and while it is installed every
        // database written plain — TodoDb.db among them — reads back as
        // ciphertext. That matters here and nowhere else: the trigger drives
        // every registered context, not just this one's.
        await session.LockAsync();

        // Whatever the probe database holds now was written under that key.
        if ((await databaseService.ListDatabasesAsync())
            .Contains(MigrationProbeContext.DatabaseName))
        {
            await databaseService.DeleteDatabaseAsync(MigrationProbeContext.DatabaseName);
        }
    }

    public override async ValueTask<string?> RunTestAsync()
    {
        try
        {
            var phase1 = await RunUnlockedPhaseAsync();
            if (phase1 is not null)
            {
                return phase1;
            }

            return await RunLockedPhaseAsync();
        }
        finally
        {
            await CleanupAsync();
        }
    }

    /// <summary>
    /// An unlocked pool: the staged database stays at V1 until something drives
    /// initialization, and driving it applies the migration.
    /// </summary>
    private async ValueTask<string?> RunUnlockedPhaseAsync()
    {
        var seeded = await StageVersion1WithDataAsync();
        if (seeded != SeedRows)
        {
            return $"FAIL[unlocked]: staged {seeded} rows, expected {SeedRows}";
        }

        await using (var staged = await Factory.CreateDbContextAsync())
        {
            if (await IndexExistsAsync(staged))
            {
                return "FAIL[unlocked]: the staged database is already at V2 — " +
                       "there is no pending migration left to observe";
            }
        }

        // What <SqliteWasmDatabaseInitializer /> does on first render. Reset
        // first: initialization already ran at boot, and without it this would
        // re-report that outcome instead of looking at what was just staged.
        Initializer.Reset();
        await Initializer.InitializeAsync();

        if (Status.State != DbInitState.READY)
        {
            return $"FAIL[unlocked]: expected READY after the trigger, got {Status.State} " +
                   $"({Status.Failure?.DefaultMessage ?? "no failure reported"})";
        }

        return await AssertUpgradedAsync("unlocked");
    }

    /// <summary>
    /// An encrypted pool, locked at boot — every encrypted boot. Initialization
    /// has to leave the work owed while the pages are ciphertext, stay that way
    /// however many times it is driven, and finish at unlock.
    /// </summary>
    private async ValueTask<string?> RunLockedPhaseAsync()
    {
        var seeded = await StageVersion1WithDataAsync();
        if (seeded != SeedRows)
        {
            return $"FAIL[locked]: staged {seeded} rows, expected {SeedRows}";
        }

        await session.EnterEncryptedAsync(VfsEncryption.VfsEncryptionTestBase.TestKey, CredentialId);
        await databaseService.CloseDatabaseAsync(MigrationProbeContext.DatabaseName);
        await session.LockAsync();

        var locked = await session.GetStateAsync();
        if (!locked.Encrypted || locked.Unlocked)
        {
            return $"FAIL[locked]: expected Encrypted+Locked before boot, got {locked}";
        }

        Initializer.Reset();
        await Initializer.InitializeAsync();

        if (Status.State != DbInitState.ENCRYPTED_LOCKED)
        {
            return $"FAIL[locked]: expected ENCRYPTED_LOCKED after initialization, " +
                   $"got {Status.State}";
        }

        // A second render drives it again. Reading pending migrations here would
        // be reading ciphertext, so this must do nothing — and, more importantly,
        // must not mark the work done.
        await Initializer.InitializeAsync();

        if (Status.State != DbInitState.ENCRYPTED_LOCKED)
        {
            return $"FAIL[locked]: driving initialization on a locked pool changed the " +
                   $"state to {Status.State}. It is not openable yet.";
        }

        await session.UnlockAsync(VfsEncryption.VfsEncryptionTestBase.TestKey);

        if (Status.State != DbInitState.READY)
        {
            return $"FAIL[locked]: expected READY after unlock, got {Status.State} " +
                   $"({Status.Failure?.DefaultMessage ?? "no failure reported"})";
        }

        return await AssertUpgradedAsync("locked");
    }

    /// <summary>V2 applied, recorded, and the rows still there.</summary>
    private async ValueTask<string?> AssertUpgradedAsync(string phase)
    {
        await using var context = await Factory.CreateDbContextAsync();

        if (!await IndexExistsAsync(context))
        {
            return $"FAIL[{phase}]: {MigrationProbeContext.V2Id} was never applied — " +
                   "the index does not exist";
        }

        var applied = (await context.Database.GetAppliedMigrationsAsync()).ToList();
        if (!applied.Contains(MigrationProbeContext.V2Id))
        {
            return $"FAIL[{phase}]: {MigrationProbeContext.V2Id} is not recorded as applied — " +
                   $"applied: [{string.Join(", ", applied)}]";
        }

        var rows = await context.Rows.CountAsync();
        if (rows != SeedRows)
        {
            return $"FAIL[{phase}]: {SeedRows} rows before the migration, {rows} after";
        }

        return null;
    }

    /// <summary>
    /// Undo the manifest this case wrote and drop its own database — and
    /// nothing else. Every other database in the pool belongs to another case,
    /// and wiping the pool here would make them all pay to rebuild.
    /// </summary>
    private async Task CleanupAsync()
    {
        try
        {
            if ((await session.GetStateAsync()).Encrypted)
            {
                await session.LeaveEncryptedAsync();
            }

            await databaseService.DeleteDatabaseAsync(MigrationProbeContext.DatabaseName);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[{Name}] cleanup failed: {ex.Message}");
        }
    }
}
