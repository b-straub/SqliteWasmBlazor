using System.Security.Cryptography;
using System.Text;

namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// Computes a deterministic SHA-256 hash over all <see cref="SyncPermission"/>
/// rows. Used by the AdminSeed tool (sign) and the worker (verify) to ensure
/// the permission table hasn't been tampered with.
///
/// <para>
/// Canonical format: rows sorted by (TableName, Role), each row serialized as
/// <c>TableName|Role|CanInsert|CanRead|CanUpdate|CanDelete|ReadonlyColumns|ReadwriteColumns\n</c>.
/// </para>
/// </summary>
public static class PermissionTableHash
{
    public static byte[] Compute(IEnumerable<SyncPermission> permissions)
    {
        var sorted = permissions
            .OrderBy(p => p.TableName, StringComparer.Ordinal)
            .ThenBy(p => (int)p.Role);

        var sb = new StringBuilder();
        foreach (var p in sorted)
        {
            sb.Append(p.TableName).Append('|');
            sb.Append((int)p.Role).Append('|');
            sb.Append(p.CanInsert ? '1' : '0').Append('|');
            sb.Append(p.CanRead ? '1' : '0').Append('|');
            sb.Append(p.CanUpdate ? '1' : '0').Append('|');
            sb.Append(p.CanDelete ? '1' : '0').Append('|');
            sb.Append(p.ReadonlyColumns).Append('|');
            sb.Append(p.ReadwriteColumns).Append('\n');
        }

        return SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
    }
}
