using Microsoft.EntityFrameworkCore;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.VfsEncryption;

/// <summary>
/// Contract test for the PRF AEAD verify-on-read path: seed an encrypted
/// DB with the canonical test key, then re-unlock with a flipped key. The
/// next read attempt fails with SQLITE_IOERR (slot 0 AEAD mismatch),
/// which the test then translates to a <see cref="PrfCredentialMismatchFailure"/>
/// on the typed boot status surface.
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
            services.GetRequiredService<ISqliteWasmDatabaseService>(),
            services.GetRequiredService<IEncryptedSqliteWasmDatabaseService>())
    {
        _reporter = services.GetRequiredService<IDbInitializationReporter>();
        _status = services.GetRequiredService<IDbInitializationStatus>();
    }

    public override string Name => "PRF_CredentialMismatchSurfacesTypedFailure";

    public override async ValueTask<string?> RunTestAsync()
    {
        // Seed the encrypted DB under the canonical test key (already set
        // as the global key by the fixture) so slot 0 is
        // AEAD-authenticated and any wrong-key attempt below has something
        // real to fail against.
        await using (var ctx = await Factory.CreateDbContextAsync())
        {
            ctx.Items.Add(new VfsTestItem { Marker = "credential-mismatch", Payload = "seed" });
            await ctx.SaveChangesAsync();
        }

        // Flip a single bit so the worker's slot-0 AEAD rejects the key
        // without any plaintext leak path. Single-key model: SetEncryptionKey
        // implicitly closes the open DB and swaps globalKey, so the next
        // open re-stamps file.key under the wrong key. The failure surfaces
        // through EF Core as SQLITE_IOERR on first read.
        var wrongKey = (byte[])TestKey.Clone();
        wrongKey[0] ^= 0x01;
        await Session.UnlockAsync(wrongKey);

        bool wrongKeyRejected = false;
        try
        {
            await using var ctx = await Factory.CreateDbContextAsync();
            _ = await ctx.Items.CountAsync();
        }
        catch
        {
            wrongKeyRejected = true;
        }

        if (!wrongKeyRejected)
        {
            return "FAIL: expected wrong-key read to throw (AEAD on slot 0)";
        }

        // Manual route: the base library does NOT auto-translate the
        // wrong-key read failure into IDbInitFailure. Apps (or a future
        // helper) must construct the failure record themselves — this is
        // the pattern we want to lock in.
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
            // Restore the canonical TestKey for subsequent tests, then
            // restore READY for downstream tests / app code. SetEncryptionKey
            // implicitly closes any open DB before swapping globalKey.
            try { await Session.UnlockAsync(TestKey); } catch { }
            _reporter.Report(DbInitState.READY);
        }
    }
}
