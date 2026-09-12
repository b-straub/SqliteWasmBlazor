namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.Migrations.Upgrade;

/// <summary>
/// The same upgrade, on an encrypted pool.
/// </summary>
/// <remarks>
/// This is the case worth measuring. A migration's work is page reads and page
/// writes, and those are exactly what the encryption VFS charges for — the
/// browse-path measurements put the multiplier near 8.6x for scan-bound work.
/// A migration that is unremarkable on a plain pool can be the slowest thing on
/// an encrypted consumer's boot path.
///
/// <para>
/// The key is worker-wide, so installing it is the whole difference: every
/// database opened afterwards, the probe included, goes through the encrypted
/// VFS. Nothing about the migrations or the assertions changes.
/// </para>
/// </remarks>
internal sealed class EncryptedPopulatedUpgradeTest(IServiceProvider services)
    : PopulatedUpgradeTest(services)
{
    public override string Name => "Migration_PopulatedDatabaseUpgradeEncrypted";

    protected override async ValueTask PrepareAsync()
    {
        var session = Services.GetRequiredService<IEncryptedSqliteWasmDatabaseService>();
        await session.UnlockAsync(VfsEncryption.VfsEncryptionTestBase.TestKey);
    }
}
