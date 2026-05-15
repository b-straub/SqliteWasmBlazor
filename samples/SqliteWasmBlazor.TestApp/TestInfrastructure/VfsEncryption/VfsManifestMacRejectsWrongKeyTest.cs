using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.VfsEncryption;

/// <summary>
/// Codex audit invariant: <c>UnlockAsync</c> on a manifested disk verifies
/// the manifest MAC under the supplied key. A wrong key must
/// <list type="bullet">
///   <item>throw before any SQL gets a chance to run,</item>
///   <item>drop the worker globalKey,</item>
///   <item>leave the disk in Encrypted+Locked with the original credentialId
///         hint preserved (so the UI can still route the user to the right
///         passkey on retry),</item>
///   <item>not corrupt the disk — re-unlocking with the correct key
///         succeeds and rows are readable.</item>
/// </list>
///
/// <para>
/// Distinct from <see cref="VfsWrongKeyFailsTest"/>: that test uses
/// <c>UnlockAsync</c> as an "install globalKey primitive" on a disk with
/// no manifest (the audit's state-aware UnlockAsync installs silently in
/// that case; the wrong-key trip surfaces later at the page-AEAD layer).
/// This test exercises the post-audit <c>EnterEncryptedAsync</c> path
/// where the manifest IS stamped, so the trip happens at unlock time
/// via <c>VerifyUnlockedManifestAsync</c>.
/// </para>
/// </summary>
internal sealed class VfsManifestMacRejectsWrongKeyTest
{
    private const int RowCount = 5;
    private const string CredentialId = "test-credential-id-manifest-mac";

    private readonly IDbContextFactory<PrfVfsTestContext> _factory;
    private readonly ISqliteWasmDatabaseService _databaseService;
    private readonly IEncryptedSqliteWasmDatabaseService _session;

    public string Name => "VFS_ManifestMacRejectsWrongKey";

    public VfsManifestMacRejectsWrongKeyTest(
        IDbContextFactory<PrfVfsTestContext> factory,
        ISqliteWasmDatabaseService databaseService,
        IEncryptedSqliteWasmDatabaseService session)
    {
        _factory = factory;
        _databaseService = databaseService;
        _session = session;
    }

    public async ValueTask<string?> RunAsync()
    {
        var dbName = PrfVfsTestContext.DatabaseName;
        await CleanupAsync();

        // Two distinct keys with non-overlapping byte patterns. The wrong
        // key MUST decode the manifest's HMAC to a different tag — using a
        // single-byte flip would also work but the pattern makes hex-dump
        // debugging clearer.
        var rightKey = new byte[32];
        for (var i = 0; i < 32; i++) rightKey[i] = (byte)(0x20 + i);

        var wrongKey = new byte[32];
        for (var i = 0; i < 32; i++) wrongKey[i] = (byte)(0xC0 + i);

        try
        {
            // Phase 1 — EnterEncryptedAsync stamps the manifest under rightKey.
            // Populate after EnterEncrypted (the empty-pool snapshot-and-heal
            // path; the EnterEncrypted-with-pre-existing-DB path is covered by
            // VfsEnterEncryptedPreExistingPlainDbTest).
            await _session.EnterEncryptedAsync(rightKey, CredentialId);
            await using (var ctx = await _factory.CreateDbContextAsync())
            {
                await ctx.Database.EnsureCreatedAsync();
                for (var i = 0; i < RowCount; i++)
                {
                    ctx.Items.Add(new VfsTestItem
                    {
                        Marker = $"macreject-{i}",
                        Payload = $"payload-{i}-{Guid.NewGuid():N}",
                    });
                }
                await ctx.SaveChangesAsync();
            }

            // Phase 2 — Lock so subsequent UnlockAsync calls hit the
            // production unlock path (state.Encrypted=true → verify MAC).
            await _databaseService.CloseDatabaseAsync(dbName);
            await _session.LockAsync();
            var locked = await _session.GetStateAsync();
            if (!locked.Encrypted || locked.Unlocked)
            {
                return $"FAIL[phase2]: expected Encrypted+Locked after Lock, got {locked}";
            }
            if (locked.Hint != CredentialId)
            {
                return $"FAIL[phase2]: expected Hint='{CredentialId}' after Lock, got '{locked.Hint}'";
            }

            // Phase 3 — UnlockAsync with the WRONG key must throw before
            // any SQL runs. The exception must surface ManifestState.TAMPERED
            // (the manifest exists; the HMAC doesn't verify).
            try
            {
                await _session.UnlockAsync(wrongKey);
                return "FAIL[phase3]: UnlockAsync(wrongKey) returned without throwing — manifest-MAC verify did not trip";
            }
            catch (InvalidOperationException ex)
            {
                if (!ex.Message.Contains("Unlock rejected", StringComparison.Ordinal))
                {
                    return $"FAIL[phase3]: expected 'Unlock rejected' in throw, got: {ex.Message}";
                }
                if (!ex.Message.Contains("TAMPERED", StringComparison.Ordinal))
                {
                    // The MAC mismatch on the wrong key maps to TAMPERED in
                    // the worker — a different state value (ABSENT / MALFORMED)
                    // would indicate the verify is firing for the wrong reason.
                    return $"FAIL[phase3]: expected ManifestState.TAMPERED in throw, got: {ex.Message}";
                }
            }

            // Phase 4 — state must be Encrypted+Locked again with the
            // original credentialId hint preserved. The audit's
            // VerifyUnlockedManifestAsync clears globalKey and reports
            // ENCRYPTED_LOCKED on bad-MAC; verify externally.
            var afterReject = await _session.GetStateAsync();
            if (!afterReject.Encrypted || afterReject.Unlocked)
            {
                return $"FAIL[phase4]: expected Encrypted+Locked after rejected unlock, got {afterReject}";
            }
            if (afterReject.Hint != CredentialId)
            {
                return $"FAIL[phase4]: hint corrupted by failed unlock — expected '{CredentialId}', got '{afterReject.Hint}'";
            }

            // Phase 5 — UnlockAsync with the CORRECT key must succeed after
            // a prior reject; the failed attempt must not have corrupted
            // the manifest or any DB.
            await _session.UnlockAsync(rightKey);
            var afterRecovery = await _session.GetStateAsync();
            if (!afterRecovery.Encrypted || !afterRecovery.Unlocked)
            {
                return $"FAIL[phase5]: expected Encrypted+Unlocked after retry with correct key, got {afterRecovery}";
            }

            // Phase 6 — rows survive the reject+retry cycle.
            List<VfsTestItem> rows;
            await using (var ctx = await _factory.CreateDbContextAsync())
            {
                rows = await ctx.Items.OrderBy(x => x.Id).ToListAsync();
            }
            if (rows.Count != RowCount)
            {
                return $"FAIL[phase6]: expected {RowCount} rows after retry, got {rows.Count}";
            }
            for (var i = 0; i < RowCount; i++)
            {
                if (rows[i].Marker != $"macreject-{i}")
                {
                    return $"FAIL[phase6]: row {i} Marker mismatch (got '{rows[i].Marker}')";
                }
            }

            return "OK";
        }
        finally
        {
            await CleanupAsync();
            CryptographicOperations.ZeroMemory(rightKey);
            CryptographicOperations.ZeroMemory(wrongKey);
        }
    }

    private async Task CleanupAsync()
    {
        try { await _session.ResetDiskAsync(); } catch { /* ignore */ }
        try
        {
            var names = await _databaseService.ListDatabasesAsync();
            foreach (var n in names)
            {
                try { await _databaseService.DeleteDatabaseAsync(n); } catch { /* ignore */ }
            }
        }
        catch { /* ignore */ }
    }
}
