using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.ImportExport;

internal class RawDatabaseImportInvalidFileTest(IDbContextFactory<TodoDbContext> factory, ISqliteWasmDatabaseService databaseService)
    : SqliteWasmTest(factory, databaseService)
{
    public override string Name => "ImportRawDatabase_InvalidFile";

    public override async ValueTask<string?> RunTestAsync()
    {
        if (DatabaseService is null)
        {
            throw new InvalidOperationException("ISqliteWasmDatabaseService not available");
        }

        // Try importing random non-SQLite bytes. ImportDatabaseAsync auto-
        // detects ciphertext vs plaintext via the "SQLite format 3" magic,
        // so random bytes are treated as opaque. The opaque path refuses
        // to overwrite an existing DB at the same path, so we delete the
        // fresh one created by RunTestWithFreshDatabaseAsync first.
        // Encrypted-DB backup restore takes the same wipe-then-import shape.
        // The error surfaces on the NEXT open, when SQLite can't parse the
        // random bytes as a valid database.
        var randomData = new byte[1024];
        Random.Shared.NextBytes(randomData);

        await DatabaseService.DeleteDatabaseAsync("TestDb.db");
        var result = await DatabaseService.ImportDatabaseAsync("TestDb.db", randomData);
        if (result != VfsImportResult.OK)
        {
            throw new InvalidOperationException(
                $"Expected ImportDatabaseAsync to write opaque bytes (OK), got {result}");
        }

        try
        {
            await using var context = await Factory.CreateDbContextAsync();
            await context.TodoItems.CountAsync();
            throw new InvalidOperationException(
                "Expected SQLite open to fail on random-bytes DB, but it succeeded");
        }
        catch (Exception ex) when (ex is not InvalidOperationException
            || !ex.Message.Contains("Expected SQLite open to fail"))
        {
            // Expected — SQLite rejects the garbage bytes as not-a-database.
        }

        return "OK";
    }
}
