using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.VfsEncryption;

/// <summary>
/// Codex audit invariant: <c>EnterEncryptedAsync</c> with a non-empty
/// pre-existing pool snapshots every plain DB <i>before</i> encrypt-in-place,
/// then stamps the manifest, then verifies. The snapshot exists to
/// power the rollback path; this test exercises the <i>happy path</i>
/// across the new orchestration to confirm pre-existing rows survive
/// the encrypt-in-place transition and the snapshot copy doesn't
/// corrupt the source.
///
/// <para>
/// <b>What this covers (and doesn't).</b> Pre-existing Plain rows are
/// readable as encrypted rows post-transition: snapshot → encrypt →
/// manifest write → verify-MAC under the same key. The <i>rollback</i>
/// branch (Phase 1 / 2 / 3 throws → <c>RollBackEnterEncryptedAsync</c>
/// restores plaintext from <c>backups</c>) requires fault injection at
/// the worker bridge — out of scope here. A bridge-decorator-based
/// fault-injection harness would close the gap; left as a follow-up.
/// </para>
/// </summary>
internal sealed class VfsEnterEncryptedPreExistingPlainDbTest
{
    private const int RowCount = 12;
    private const string CredentialId = "test-credential-id-preexisting-plain";

    private readonly IDbContextFactory<PrfVfsTestContext> _factory;
    private readonly ISqliteWasmDatabaseService _databaseService;
    private readonly IEncryptedSqliteWasmDatabaseService _session;

    public string Name => "VFS_EnterEncrypted_PreExistingPlainDb";

    public VfsEnterEncryptedPreExistingPlainDbTest(
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

        var key = new byte[32];
        for (var i = 0; i < 32; i++) key[i] = (byte)(0x40 + i);

        try
        {
            // Phase 1 — populate the Plain disk with deterministic rows.
            // No EnterEncryptedAsync yet — the pool is intentionally not
            // empty when Phase 2 runs, so the snapshot loop has DBs to
            // copy.
            await using (var ctx = await _factory.CreateDbContextAsync())
            {
                await ctx.Database.EnsureCreatedAsync();
                for (var i = 0; i < RowCount; i++)
                {
                    ctx.Items.Add(new VfsTestItem
                    {
                        Marker = $"preexisting-{i}",
                        Payload = $"payload-{i}-{Guid.NewGuid():N}",
                    });
                }
                await ctx.SaveChangesAsync();
            }
            await _databaseService.CloseDatabaseAsync(dbName);

            // Pre-condition: state is Plain.
            var pre = await _session.GetStateAsync();
            if (pre.Encrypted)
            {
                return $"FAIL[phase1]: expected Plain disk before EnterEncrypted, got {pre}";
            }
            var listBefore = await _databaseService.ListDatabasesAsync();
            if (!listBefore.Contains(dbName))
            {
                return $"FAIL[phase1]: pool must contain '{dbName}' to exercise the snapshot loop, got [{string.Join(',', listBefore)}]";
            }

            // Phase 2 — EnterEncryptedAsync on a non-empty pool.
            // Codex's flow: Phase 0 snapshot → Phase 1 encrypt-in-place
            // → Phase 2 install key → Phase 3 write+verify manifest.
            await _session.EnterEncryptedAsync(key, CredentialId);

            var post = await _session.GetStateAsync();
            if (!post.Encrypted || !post.Unlocked)
            {
                return $"FAIL[phase2]: expected Encrypted+Unlocked after EnterEncrypted, got {post}";
            }
            if (post.Hint != CredentialId)
            {
                return $"FAIL[phase2]: hint mismatch — expected '{CredentialId}', got '{post.Hint}'";
            }

            // Phase 3 — pre-existing rows must be readable through the
            // encrypted hot path. If the snapshot phase corrupted the
            // source, or encrypt-in-place dropped pages, row count would
            // be off or payloads would mismatch.
            List<VfsTestItem> rows;
            await using (var ctx = await _factory.CreateDbContextAsync())
            {
                rows = await ctx.Items.OrderBy(x => x.Id).ToListAsync();
            }
            if (rows.Count != RowCount)
            {
                return $"FAIL[phase3]: expected {RowCount} rows after encrypt-in-place, got {rows.Count}";
            }
            for (var i = 0; i < RowCount; i++)
            {
                if (rows[i].Marker != $"preexisting-{i}")
                {
                    return $"FAIL[phase3]: row {i} Marker mismatch (got '{rows[i].Marker}')";
                }
                if (!rows[i].Payload.StartsWith($"payload-{i}-", StringComparison.Ordinal))
                {
                    return $"FAIL[phase3]: row {i} Payload mismatch (got '{rows[i].Payload}')";
                }
            }

            // Phase 4 — Lock + Unlock cycle confirms the manifest stamped
            // in Phase 3 verifies under the same key. If WriteManifestAsync
            // had landed bad bytes, the post-Lock UnlockAsync would now
            // throw "Unlock rejected" — covered explicitly in
            // VfsManifestMacRejectsWrongKeyTest with a *wrong* key.
            await _databaseService.CloseDatabaseAsync(dbName);
            await _session.LockAsync();
            await _session.UnlockAsync(key);

            await using (var ctx = await _factory.CreateDbContextAsync())
            {
                var countAfterCycle = await ctx.Items.CountAsync();
                if (countAfterCycle != RowCount)
                {
                    return $"FAIL[phase4]: rows lost across Lock/Unlock cycle (got {countAfterCycle})";
                }
            }

            return "OK";
        }
        finally
        {
            await CleanupAsync();
            CryptographicOperations.ZeroMemory(key);
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
