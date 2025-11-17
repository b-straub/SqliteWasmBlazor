using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models.Models;

namespace SqliteWasmBlazor.Models.Extensions;

/// <summary>
/// Extension methods for managing sync checkpoints in TodoDbContext.
/// </summary>
public static class CheckpointExtensions
{
    /// <summary>
    /// Creates a new checkpoint in the database with current item counts.
    /// </summary>
    /// <param name="context">The database context.</param>
    /// <param name="description">Description of the checkpoint.</param>
    /// <param name="checkpointType">Type of checkpoint (Auto or Manual).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The created checkpoint.</returns>
    public static async Task<SyncState> CreateCheckpointAsync(
        this TodoDbContext context,
        string description,
        string checkpointType = "Auto",
        CancellationToken cancellationToken = default)
    {
        // Count active and deleted items
        var activeCount = await context.TodoItems
            .CountAsync(t => !t.IsDeleted, cancellationToken);

        var tombstoneCount = await context.TodoItems
            .CountAsync(t => t.IsDeleted, cancellationToken);

        // Create checkpoint
        var checkpoint = new SyncState
        {
            CreatedAt = DateTime.UtcNow,
            Description = description,
            ActiveItemCount = activeCount,
            TombstoneCount = tombstoneCount,
            CheckpointType = checkpointType
        };

        context.SyncState.Add(checkpoint);
        await context.SaveChangesAsync(cancellationToken);

        return checkpoint;
    }
}
