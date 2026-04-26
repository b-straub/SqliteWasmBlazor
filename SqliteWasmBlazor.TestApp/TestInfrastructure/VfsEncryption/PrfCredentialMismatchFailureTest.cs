using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.VfsEncryption;

/// <summary>
/// Contract test for the PRF AEAD verify-on-install path: seed an encrypted
/// DB with the canonical test key, then re-install with a flipped key. The
/// worker bridge returns <see cref="VfsKeyInstallResult.WRONG_KEY"/>, which
/// the call site converts to a <see cref="PrfCredentialMismatchFailure"/> on
/// the typed boot status surface.
///
/// Proves the discriminator round-trip end-to-end: the
/// <see cref="IDbInitFailure"/> can be constructed by a caller, reported
/// via <see cref="IDbInitializationReporter"/>, and pattern-matched on
/// <see cref="IDbInitializationStatus.Failure"/> by a consumer — the same
/// shape the demo's <c>DatabaseErrorAlert</c> uses.
/// </summary>
internal sealed class PrfCredentialMismatchFailureTest : VfsEncryptionTestBase
{
    private readonly IDbInitializationReporter _reporter;
    private readonly IDbInitializationStatus _status;

    public PrfCredentialMismatchFailureTest(IServiceProvider services)
        : base(
            services.GetRequiredService<IDbContextFactory<EncryptedTestContext>>(),
            services.GetRequiredService<ISqliteWasmDatabaseService>())
    {
        _reporter = services.GetRequiredService<IDbInitializationReporter>();
        _status = services.GetRequiredService<IDbInitializationStatus>();
    }

    public override string Name => "PRF_CredentialMismatchSurfacesTypedFailure";

    public override async ValueTask<string?> RunTestAsync()
    {
        // Seed the encrypted DB under the canonical test key so slot 0 is
        // AEAD-authenticated and any wrong-key attempt below has something
        // real to fail against.
        await using (var ctx = await Factory.CreateDbContextAsync())
        {
            ctx.Items.Add(new VfsTestItem { Marker = "credential-mismatch", Payload = "seed" });
            await ctx.SaveChangesAsync();
        }
        await DatabaseService.CloseDatabaseAsync(EncryptedDatabaseName);
        await DatabaseService.ClearEncryptionKeyAsync(EncryptedDatabaseName);

        // Flip a single bit so the worker's slot-0 verify rejects the key
        // without any plaintext leak path.
        var wrongKey = (byte[])TestKey.Clone();
        wrongKey[0] ^= 0x01;

        var result = await DatabaseService.InstallEncryptionKeyAsync(EncryptedDatabaseName, wrongKey);

        if (result != VfsKeyInstallResult.WRONG_KEY)
        {
            return $"FAIL: expected VfsKeyInstallResult.WRONG_KEY, got {result}";
        }

        // Manual route: the base library does NOT auto-translate
        // VfsKeyInstallResult into IDbInitFailure. Apps (or a future helper)
        // must construct the failure record themselves — this is the pattern
        // we want to lock in.
        _reporter.Report(DbInitState.NOT_STARTED);
        _reporter.Report(
            DbInitState.FAILED,
            new PrfCredentialMismatchFailure(EncryptedDatabaseName));

        try
        {
            if (_status.State != DbInitState.FAILED)
            {
                return $"FAIL: expected FAILED state after manual report, got {_status.State}";
            }

            if (_status.Failure is not PrfCredentialMismatchFailure mismatch)
            {
                return $"FAIL: expected PrfCredentialMismatchFailure, got " +
                       $"{_status.Failure?.GetType().Name ?? "null"}";
            }

            if (!string.Equals(mismatch.DatabaseName, EncryptedDatabaseName, StringComparison.Ordinal))
            {
                return $"FAIL: expected DatabaseName='{EncryptedDatabaseName}', " +
                       $"got '{mismatch.DatabaseName}'";
            }

            if (string.IsNullOrWhiteSpace(mismatch.DefaultMessage))
            {
                return "FAIL: PrfCredentialMismatchFailure.DefaultMessage is empty";
            }

            return null;
        }
        finally
        {
            // Clear the stale registry entry so subsequent tests get a clean
            // slot, then restore READY for downstream tests / app code.
            try { await DatabaseService.ClearEncryptionKeyAsync(EncryptedDatabaseName); } catch { }
            _reporter.Report(DbInitState.READY);
        }
    }
}
