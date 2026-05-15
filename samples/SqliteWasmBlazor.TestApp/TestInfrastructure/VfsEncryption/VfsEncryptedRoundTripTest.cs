using Microsoft.EntityFrameworkCore;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.VfsEncryption;

/// <summary>
/// End-to-end smoke test: create an encrypted DB, insert rows, close,
/// reopen with the same key, read back. Exercises the whole pipeline —
/// C# VfsKeyHeader → worker unpack → key registry → VFS xOpen with key
/// → page encryption on INSERT → page decryption on SELECT — against
/// real OPFS SAHPool and the real ChaCha20-Poly1305 primitive.
/// </summary>
internal sealed class VfsEncryptedRoundTripTest(
    IDbContextFactory<EncryptedTestContext> factory,
    ISqliteWasmDatabaseService databaseService,
    IEncryptedSqliteWasmDatabaseService session)
    : VfsEncryptionTestBase(factory, databaseService, session)
{
    public override string Name => "VFS_EncryptedRoundTrip";

    public override async ValueTask<string?> RunTestAsync()
    {
        const int rowCount = 25;

        // Seed
        await using (var ctx = await Factory.CreateDbContextAsync())
        {
            for (var i = 0; i < rowCount; i++)
            {
                ctx.Items.Add(new VfsTestItem
                {
                    Marker = $"marker-{i}",
                    Payload = $"payload-{i}-{Guid.NewGuid():N}",
                });
            }
            await ctx.SaveChangesAsync();

            // The VFS encrypts WAL frames with the same envelope as the main
            // DB, so encrypted DBs run in journal_mode=WAL (crash-safe).
            // Verifying here catches regressions where someone flips the
            // encrypted path back to journal_mode=MEMORY.
            var conn = ctx.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
            {
                await conn.OpenAsync();
            }
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode;";
            var mode = (await cmd.ExecuteScalarAsync())?.ToString();
            if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
            {
                return $"Expected journal_mode=wal on encrypted DB, got '{mode}'";
            }
        }

        // Close worker-side handle so the next open is a true reopen.
        await DatabaseService.CloseDatabaseAsync(EncryptedDatabaseName);

        // Reopen + read back.
        List<VfsTestItem> rows;
        await using (var ctx = await Factory.CreateDbContextAsync())
        {
            rows = await ctx.Items.OrderBy(x => x.Id).ToListAsync();
        }

        if (rows.Count != rowCount)
        {
            return $"Expected {rowCount} rows after reopen, got {rows.Count}";
        }
        for (var i = 0; i < rowCount; i++)
        {
            if (rows[i].Marker != $"marker-{i}")
            {
                return $"Row {i}: Marker mismatch (got '{rows[i].Marker}')";
            }
            if (!rows[i].Payload.StartsWith($"payload-{i}-"))
            {
                return $"Row {i}: Payload mismatch (got '{rows[i].Payload}')";
            }
        }

        return null;
    }
}
