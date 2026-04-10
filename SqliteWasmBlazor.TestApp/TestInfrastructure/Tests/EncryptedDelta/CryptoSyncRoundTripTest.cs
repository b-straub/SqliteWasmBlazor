using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.TestApp.TestInfrastructure.CryptoSync;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.EncryptedDelta;

/// <summary>
/// Full encrypted delta roundtrip via SyncOrchestrator: Export → delete → Import → verify.
/// Pending SyncOrchestrator V2 implementation.
/// </summary>
internal class CryptoSyncRoundTripTest(
    IDbContextFactory<CryptoTestContext> cryptoFactory,
    ISqliteWasmDatabaseService databaseService)
    : CryptoSyncTestBase(cryptoFactory, databaseService)
{
    public override string Name => "CryptoSync_RoundTrip";

    public override ValueTask<string?> RunTestAsync()
    {
        return new ValueTask<string?>("PENDING: SyncOrchestrator V2 implementation");
    }
}
