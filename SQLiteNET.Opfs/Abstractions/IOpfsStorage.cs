namespace SQLiteNET.Opfs.Abstractions;

/// <summary>
/// Abstraction for OPFS storage operations with EF Core integration.
/// Provides persistent storage for SQLite databases using Origin Private File System (OPFS).
/// </summary>
public interface IOpfsStorage
{
    /// <summary>
    /// Check if OPFS is initialized and ready
    /// </summary>
    bool IsReady { get; }

    /// <summary>
    /// Check if incremental sync (VFS tracking) is available
    /// </summary>
    bool IsIncrementalSyncEnabled { get; }

    /// <summary>
    /// Set to true to temporarily disable incremental sync and use full sync instead.
    /// Useful for performance testing and debugging.
    /// </summary>
    bool ForceFullSync { get; set; }

    /// <summary>
    /// Control logging verbosity for OPFS operations.
    /// Default: Warning (only errors and warnings).
    /// Set to Info or Debug for more detailed output.
    /// </summary>
    OpfsLogLevel LogLevel { get; set; }

    /// <summary>
    /// Initialize OPFS storage. Can be called multiple times safely.
    /// </summary>
    Task<bool> InitializeAsync();

    /// <summary>
    /// Persist a database file from Emscripten MEMFS to OPFS.
    /// This method is called automatically after write operations (INSERT, UPDATE, DELETE).
    /// </summary>
    /// <param name="fileName">Database filename (e.g., "myapp.db")</param>
    Task Persist(string fileName);

    /// <summary>
    /// Load a database file from OPFS to Emscripten MEMFS.
    /// This method is called during application startup to restore persisted data.
    /// </summary>
    /// <param name="fileName">Database filename (e.g., "myapp.db")</param>
    Task Load(string fileName);

    /// <summary>
    /// Pause automatic persistence for batch operations.
    /// Call this before executing multiple write operations to avoid excessive syncs.
    /// </summary>
    void PauseAutomaticPersistent();

    /// <summary>
    /// Resume automatic persistence and flush any pending changes.
    /// Call this after batch operations complete to persist all changes at once.
    /// </summary>
    Task ResumeAutomaticPersistent();

    /// <summary>
    /// Get list of database files in OPFS
    /// </summary>
    Task<string[]> GetFileListAsync();

    /// <summary>
    /// Export a database file from OPFS as byte array
    /// </summary>
    Task<byte[]> ExportDatabaseAsync(string filename);

    /// <summary>
    /// Import a database file to OPFS from byte array
    /// </summary>
    Task<int> ImportDatabaseAsync(string filename, byte[] data);

    /// <summary>
    /// Get current SAH pool capacity
    /// </summary>
    Task<int> GetCapacityAsync();

    /// <summary>
    /// Add capacity to SAH pool
    /// </summary>
    Task<int> AddCapacityAsync(int count);
}
