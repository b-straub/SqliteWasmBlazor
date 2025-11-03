// System.Data.SQLite.Wasm - ADO.NET provider for SQLite via WASM + OPFS
// Based on System.Data.SQLite (Public Domain)

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace System.Data.SQLite.Wasm;

/// <summary>
/// WASM handle types using IntPtr IDs that map to objects on JavaScript side.
/// This preserves compatibility with System.Data.SQLite's CriticalHandle architecture.
///
/// Architecture:
/// - C# side: Uses IntPtr (integer IDs) wrapped in CriticalHandle
/// - JS side: Maintains Map&lt;id, sqlite3_object&gt; registry
/// - Handles passed as numbers between C# and JS
/// </summary>
[SupportedOSPlatform("browser")]
internal sealed class SQLiteConnectionHandle : CriticalHandle
{
    private bool ownHandle;

    public SQLiteConnectionHandle() : base(IntPtr.Zero)
    {
        ownHandle = true;
    }

    public SQLiteConnectionHandle(IntPtr handle) : base(IntPtr.Zero)
    {
        SetHandle(handle);
        ownHandle = true;
    }

    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle()
    {
        if (ownHandle && !IsInvalid)
        {
            // Close will be handled by SQLite3.CloseConnection
            return true;
        }
        return false;
    }

    public bool OwnHandle
    {
        get { return ownHandle; }
        set { ownHandle = value; }
    }
}

/// <summary>
/// Represents a SQLite prepared statement handle in WASM.
/// </summary>
[SupportedOSPlatform("browser")]
internal sealed class SQLiteStatementHandle : CriticalHandle
{
    public SQLiteStatementHandle() : base(IntPtr.Zero)
    {
    }

    public SQLiteStatementHandle(IntPtr handle) : base(IntPtr.Zero)
    {
        SetHandle(handle);
    }

    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle()
    {
        // Finalize will be handled by SQLite3
        return true;
    }
}

/// <summary>
/// Represents a SQLite BLOB handle in WASM.
/// </summary>
[SupportedOSPlatform("browser")]
internal sealed class SQLiteBlobHandle : CriticalHandle
{
    public SQLiteBlobHandle() : base(IntPtr.Zero)
    {
    }

    public SQLiteBlobHandle(IntPtr handle) : base(IntPtr.Zero)
    {
        SetHandle(handle);
    }

    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle()
    {
        // Close will be handled by SQLite3
        return true;
    }
}

/// <summary>
/// Represents a SQLite backup handle in WASM.
/// </summary>
[SupportedOSPlatform("browser")]
internal sealed class SQLiteBackupHandle : CriticalHandle
{
    public SQLiteBackupHandle() : base(IntPtr.Zero)
    {
    }

    public SQLiteBackupHandle(IntPtr handle) : base(IntPtr.Zero)
    {
        SetHandle(handle);
    }

    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle()
    {
        // Finish will be handled by SQLite3
        return true;
    }
}
