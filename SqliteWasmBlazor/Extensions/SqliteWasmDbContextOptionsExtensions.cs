// SqliteWasmBlazor - Minimal EF Core compatible provider
// MIT License

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;

namespace SqliteWasmBlazor;

/// <summary>
/// Extension methods for configuring SqliteWasm provider with EF Core.
/// </summary>
public static class SqliteWasmDbContextOptionsExtensions
{
    /// <summary>
    /// Configures the DbContext to use the SqliteWasm provider with the specified connection.
    /// </summary>
    /// <param name="optionsBuilder">The builder being used to configure the context</param>
    /// <param name="connection">The SqliteWasmConnection to use</param>
    /// <returns>The options builder for chaining</returns>
    public static DbContextOptionsBuilder UseSqliteWasm(
        this DbContextOptionsBuilder optionsBuilder,
        SqliteWasmConnection connection)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        ArgumentNullException.ThrowIfNull(connection);

        // Use the standard EF Core Sqlite provider with our custom connection
        // The connection handles all the worker bridge communication
        optionsBuilder.UseSqlite(connection);

        // Replace the database creator to handle OPFS storage
        // This enables EnsureDeletedAsync/EnsureCreatedAsync to work with OPFS
        optionsBuilder.ReplaceService<IRelationalDatabaseCreator, SqliteWasmDatabaseCreator>();

        // Replace the history repository to disable migration locking
        // The EF Core migration lock mechanism causes infinite polling in WASM
        optionsBuilder.ReplaceService<IHistoryRepository, SqliteWasmHistoryRepository>();

        return optionsBuilder;
    }

    /// <inheritdoc cref="UseSqliteWasm(DbContextOptionsBuilder, SqliteWasmConnection)"/>
    public static DbContextOptionsBuilder<TContext> UseSqliteWasm<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder,
        SqliteWasmConnection connection)
        where TContext : DbContext
    {
        ((DbContextOptionsBuilder)optionsBuilder).UseSqliteWasm(connection);
        return optionsBuilder;
    }

    /// <inheritdoc cref="UseSqliteWasm(DbContextOptionsBuilder, string)"/>
    public static DbContextOptionsBuilder<TContext> UseSqliteWasm<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder,
        string connectionString)
        where TContext : DbContext
    {
        ((DbContextOptionsBuilder)optionsBuilder).UseSqliteWasm(connectionString);
        return optionsBuilder;
    }

    /// <summary>
    /// Configures the DbContext to use the SqliteWasm provider with the specified connection string.
    /// </summary>
    /// <param name="optionsBuilder">The builder being used to configure the context</param>
    /// <param name="connectionString">The connection string (e.g., "Data Source=MyDb.db")</param>
    /// <returns>The options builder for chaining</returns>
    public static DbContextOptionsBuilder UseSqliteWasm(
        this DbContextOptionsBuilder optionsBuilder,
        string connectionString)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("Connection string cannot be null or empty", nameof(connectionString));
        }

        var connection = new SqliteWasmConnection(connectionString);
        return optionsBuilder.UseSqliteWasm(connection);
    }

    /// <summary>
    /// Configures the DbContext to use the SqliteWasm provider with PRF-keyed
    /// at-rest encryption. The key is stamped on the underlying connection so
    /// every open through this DbContext routes through the encrypted VFS path.
    /// </summary>
    /// <param name="optionsBuilder">The builder being used to configure the context.</param>
    /// <param name="connectionString">Connection string (e.g., "Data Source=MyDb.db").</param>
    /// <param name="encryptionKey">
    /// 32-byte ChaCha20-Poly1305 key. Derive via BlazorPRF's
    /// <c>deriveHkdfKey(prfSeed, "prf-vfs-v1|" + dbPath, 32)</c> or equivalent.
    /// </param>
    /// <returns>The options builder for chaining.</returns>
    public static DbContextOptionsBuilder UseSqliteWasm(
        this DbContextOptionsBuilder optionsBuilder,
        string connectionString,
        ReadOnlyMemory<byte> encryptionKey)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("Connection string cannot be null or empty", nameof(connectionString));
        }
        if (encryptionKey.Length != 32)
        {
            throw new ArgumentException(
                $"encryptionKey must be exactly 32 bytes, got {encryptionKey.Length}",
                nameof(encryptionKey));
        }

        var connection = new SqliteWasmConnection(connectionString)
        {
            EncryptionKey = encryptionKey,
        };
        return optionsBuilder.UseSqliteWasm(connection);
    }

    /// <inheritdoc cref="UseSqliteWasm(DbContextOptionsBuilder, string, ReadOnlyMemory{byte})"/>
    public static DbContextOptionsBuilder<TContext> UseSqliteWasm<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder,
        string connectionString,
        ReadOnlyMemory<byte> encryptionKey)
        where TContext : DbContext
    {
        ((DbContextOptionsBuilder)optionsBuilder).UseSqliteWasm(connectionString, encryptionKey);
        return optionsBuilder;
    }

    /// <summary>
    /// Configures the DbContext to use the SqliteWasm provider with a
    /// user-supplied password. The worker derives the 32-byte VFS key via
    /// Argon2id on first open (creating a per-DB salt in the SAHPool header)
    /// and reuses the same salt on subsequent opens so the password is
    /// reproducible.
    /// </summary>
    /// <param name="optionsBuilder">Context options builder.</param>
    /// <param name="connectionString">Connection string (e.g., "Data Source=MyDb.db").</param>
    /// <param name="password">
    /// UTF-8 encoded password bytes. Stored on the connection for the open
    /// call and zeroized by the bridge after the worker derives the key.
    /// </param>
    public static DbContextOptionsBuilder UseSqliteWasmPassword(
        this DbContextOptionsBuilder optionsBuilder,
        string connectionString,
        ReadOnlyMemory<byte> password)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("Connection string cannot be null or empty", nameof(connectionString));
        }
        if (password.Length == 0)
        {
            throw new ArgumentException("password must be non-empty", nameof(password));
        }

        var connection = new SqliteWasmConnection(connectionString)
        {
            EncryptionPassword = password,
        };
        return optionsBuilder.UseSqliteWasm(connection);
    }

    /// <inheritdoc cref="UseSqliteWasmPassword(DbContextOptionsBuilder, string, ReadOnlyMemory{byte})"/>
    public static DbContextOptionsBuilder<TContext> UseSqliteWasmPassword<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder,
        string connectionString,
        ReadOnlyMemory<byte> password)
        where TContext : DbContext
    {
        ((DbContextOptionsBuilder)optionsBuilder).UseSqliteWasmPassword(connectionString, password);
        return optionsBuilder;
    }
}
