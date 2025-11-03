// System.Data.SQLite.Wasm - ADO.NET provider for SQLite via WASM + OPFS
// Based on System.Data.SQLite (Public Domain)

using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

namespace System.Data.SQLite.Wasm;

/// <summary>
/// JSImport interop layer for sqlite-wasm running in Web Worker with OPFS.
/// This class replaces UnsafeNativeMethods.cs from System.Data.SQLite.
/// Instead of P/Invoke to native sqlite3.dll, we use JSImport to call
/// the sqlite-wasm library running in a worker with OPFS SAHPool VFS.
/// </summary>
[SupportedOSPlatform("browser")]
internal static partial class SqliteWasmInterop
{
    private const string ModuleName = "sqliteWasmWorker";

    private static bool _isInitialized;

    /// <summary>
    /// Initialize the sqlite-wasm worker and OPFS storage.
    /// Must be called once before using any SQLite operations.
    /// </summary>
    public static async Task InitializeAsync()
    {
        if (_isInitialized)
        {
            return;
        }

        await JSHost.ImportAsync(
            ModuleName,
            "/_content/SqliteWasm.Data/sqlite-wasm-worker.js");

        _isInitialized = true;
    }

    #region Core SQLite Functions - Connection Management

    /// <summary>
    /// Open a database connection.
    /// Returns an integer ID that maps to the db object on JS side.
    /// </summary>
    [JSImport("sqlite3_open", ModuleName)]
    [return: JSMarshalAs<JSType.Number>]
    public static partial Task<IntPtr> Sqlite3OpenAsync(
        string filename,
        int flags);

    /// <summary>
    /// Close a database connection.
    /// Takes an integer ID representing the db handle.
    /// </summary>
    [JSImport("sqlite3_close", ModuleName)]
    public static partial Task<int> Sqlite3CloseAsync(
        [JSMarshalAs<JSType.Number>] IntPtr db);

    #endregion

    #region Core SQLite Functions - Statement Preparation & Execution

    /// <summary>
    /// Prepare a SQL statement.
    /// Returns statement handle ID.
    /// </summary>
    [JSImport("sqlite3_prepare_v2", ModuleName)]
    [return: JSMarshalAs<JSType.Number>]
    public static partial Task<IntPtr> Sqlite3PrepareAsync(
        [JSMarshalAs<JSType.Number>] IntPtr db,
        string sql);

    /// <summary>
    /// Execute a prepared statement (fetch one row).
    /// Returns: SQLITE_ROW (100) or SQLITE_DONE (101)
    /// </summary>
    [JSImport("sqlite3_step", ModuleName)]
    public static partial Task<int> Sqlite3StepAsync(
        [JSMarshalAs<JSType.Number>] IntPtr stmt);

    /// <summary>
    /// Finalize (destroy) a prepared statement.
    /// </summary>
    [JSImport("sqlite3_finalize", ModuleName)]
    public static partial Task<int> Sqlite3FinalizeAsync(
        [JSMarshalAs<JSType.Number>] IntPtr stmt);

    /// <summary>
    /// Reset a prepared statement for re-execution.
    /// </summary>
    [JSImport("sqlite3_reset", ModuleName)]
    public static partial Task<int> Sqlite3ResetAsync(
        [JSMarshalAs<JSType.Number>] IntPtr stmt);

    #endregion

    #region Parameter Binding

    /// <summary>
    /// Bind a NULL value to a parameter.
    /// </summary>
    [JSImport("sqlite3_bind_null", ModuleName)]
    public static partial Task<int> Sqlite3BindNullAsync(
        [JSMarshalAs<JSType.Number>] IntPtr stmt,
        int index);

    /// <summary>
    /// Bind an integer value to a parameter.
    /// </summary>
    [JSImport("sqlite3_bind_int64", ModuleName)]
    public static partial Task<int> Sqlite3BindInt64Async(
        [JSMarshalAs<JSType.Number>] IntPtr stmt,
        int index,
        [JSMarshalAs<JSType.Number>] long value);

    /// <summary>
    /// Bind a double value to a parameter.
    /// </summary>
    [JSImport("sqlite3_bind_double", ModuleName)]
    public static partial Task<int> Sqlite3BindDoubleAsync(
        [JSMarshalAs<JSType.Number>] IntPtr stmt,
        int index,
        double value);

    /// <summary>
    /// Bind a text value to a parameter.
    /// </summary>
    [JSImport("sqlite3_bind_text", ModuleName)]
    public static partial Task<int> Sqlite3BindTextAsync(
        [JSMarshalAs<JSType.Number>] IntPtr stmt,
        int index,
        string value);

    /// <summary>
    /// Bind a blob value to a parameter.
    /// </summary>
    [JSImport("sqlite3_bind_blob", ModuleName)]
    public static partial Task<int> Sqlite3BindBlobAsync(
        [JSMarshalAs<JSType.Number>] IntPtr stmt,
        int index,
        byte[] value);

    #endregion

    #region Column Access

    /// <summary>
    /// Get the number of columns in a result set.
    /// </summary>
    [JSImport("sqlite3_column_count", ModuleName)]
    public static partial int Sqlite3ColumnCount(JSObject stmt);

    /// <summary>
    /// Get column name.
    /// </summary>
    [JSImport("sqlite3_column_name", ModuleName)]
    public static partial string Sqlite3ColumnName(
        JSObject stmt,
        int index);

    /// <summary>
    /// Get column type.
    /// </summary>
    [JSImport("sqlite3_column_type", ModuleName)]
    public static partial int Sqlite3ColumnType(
        JSObject stmt,
        int index);

    /// <summary>
    /// Get integer column value.
    /// </summary>
    [JSImport("sqlite3_column_int64", ModuleName)]
    [return: JSMarshalAs<JSType.Number>]
    public static partial long Sqlite3ColumnInt64(
        JSObject stmt,
        int index);

    /// <summary>
    /// Get double column value.
    /// </summary>
    [JSImport("sqlite3_column_double", ModuleName)]
    public static partial double Sqlite3ColumnDouble(
        JSObject stmt,
        int index);

    /// <summary>
    /// Get text column value.
    /// </summary>
    [JSImport("sqlite3_column_text", ModuleName)]
    public static partial string Sqlite3ColumnText(
        JSObject stmt,
        int index);

    /// <summary>
    /// Get blob column value.
    /// </summary>
    [JSImport("sqlite3_column_blob", ModuleName)]
    public static partial byte[] Sqlite3ColumnBlob(
        JSObject stmt,
        int index);

    #endregion

    #region Error Handling

    /// <summary>
    /// Get error message from database connection.
    /// </summary>
    [JSImport("sqlite3_errmsg", ModuleName)]
    public static partial string Sqlite3Errmsg(JSObject db);

    /// <summary>
    /// Get last error code.
    /// </summary>
    [JSImport("sqlite3_errcode", ModuleName)]
    public static partial int Sqlite3Errcode(JSObject db);

    #endregion

    #region Transaction Support

    /// <summary>
    /// Get last insert rowid.
    /// </summary>
    [JSImport("sqlite3_last_insert_rowid", ModuleName)]
    [return: JSMarshalAs<JSType.Number>]
    public static partial long Sqlite3LastInsertRowid(JSObject db);

    /// <summary>
    /// Get number of changes from last statement.
    /// </summary>
    [JSImport("sqlite3_changes", ModuleName)]
    public static partial int Sqlite3Changes(JSObject db);

    #endregion

    // TODO: Add more functions as needed to match UnsafeNativeMethods.cs
    // Total from System.Data.SQLite: ~445 DllImport declarations
    // We'll implement them incrementally as we encounter them
}
