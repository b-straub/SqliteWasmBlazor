using Microsoft.EntityFrameworkCore;
using System.Text;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.VfsEncryption;

/// <summary>
/// Write recognizable plaintext to the DB, close, export the raw OPFS
/// bytes, assert the plaintext markers and the SQLite format-3 magic are
/// both absent. This is the test that proves the encryption actually
/// happens at rest.
/// </summary>
internal sealed class VfsOnDiskCiphertextTest(
    IDbContextFactory<EncryptedTestContext> factory,
    ISqliteWasmDatabaseService databaseService,
    IEncryptedSqliteWasmDatabaseService session)
    : VfsEncryptionTestBase(factory, databaseService, session)
{
    public override string Name => "VFS_OnDiskCiphertext";

    private const string MarkerNeedle = "VFS_CIPHERTEXT_TEST_MARKER_7A5C";
    private static readonly byte[] SqliteMagic =
        Encoding.ASCII.GetBytes("SQLite format 3\0");

    public override async ValueTask<string?> RunTestAsync()
    {
        // Seed with highly identifiable strings.
        await using (var ctx = await Factory.CreateDbContextAsync())
        {
            for (var i = 0; i < 20; i++)
            {
                ctx.Items.Add(new VfsTestItem
                {
                    Marker = $"{MarkerNeedle}-{i}",
                    Payload = $"{MarkerNeedle}-payload-{i}",
                });
            }
            await ctx.SaveChangesAsync();
        }

        // Dump raw OPFS bytes. ExportDatabaseAsync reads the SAH from
        // HEADER_OFFSET_DATA onwards, which for our encrypted DBs is the
        // full sequence of 4124-byte encrypted physical slots (ciphertext
        // 4096 + nonce 12 + tag 16).
        var bytes = await DatabaseService.ExportDatabaseAsync(EncryptedDatabaseName);

        // One full physical slot minimum.
        const int PhysicalSlotSize = 4124;
        const int PagePlaintextLen = 4096;
        const int NonceTagTailLen = 28;

        if (bytes.Length < PhysicalSlotSize)
        {
            return $"Exported bytes suspiciously short: {bytes.Length} (expected >= {PhysicalSlotSize})";
        }

        // 1. Plaintext marker must not appear anywhere in the ciphertext.
        var needleBytes = Encoding.UTF8.GetBytes(MarkerNeedle);
        if (IndexOf(bytes, needleBytes) >= 0)
        {
            return $"FAIL: plaintext marker '{MarkerNeedle}' found in on-disk bytes — data is NOT encrypted at rest";
        }

        // 2. SQLite header magic must not appear at offset 0 (encrypted DBs
        //    have ciphertext at the start of slot 0, not the format-3 magic).
        if (bytes.Length >= 16 && bytes.AsSpan(0, 16).SequenceEqual(SqliteMagic))
        {
            return "FAIL: 'SQLite format 3' magic at offset 0 of exported bytes — slot 0 is NOT encrypted";
        }

        // 3. Every 4124-byte physical slot ends with 28 bytes of nonce+tag.
        //    Random nonces are overwhelmingly unlikely to be all-zero.
        var firstSlotTail = bytes.AsSpan(PagePlaintextLen, NonceTagTailLen);
        var allZero = true;
        foreach (var b in firstSlotTail)
        {
            if (b != 0) { allZero = false; break; }
        }
        if (allZero)
        {
            return "FAIL: slot-0 nonce+tag tail is all zero — encryption envelope missing";
        }

        Console.WriteLine($"[{Name}] {bytes.Length} bytes exported, slot-0 nonce+tag tail OK, no plaintext markers found");
        return null;
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length) return -1;
        var limit = haystack.Length - needle.Length;
        for (var i = 0; i <= limit; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { match = false; break; }
            }
            if (match) return i;
        }
        return -1;
    }
}
