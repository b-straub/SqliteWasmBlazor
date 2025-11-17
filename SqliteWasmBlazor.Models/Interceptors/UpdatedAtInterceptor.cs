using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SqliteWasmBlazor.Models.Models;

namespace SqliteWasmBlazor.Models.Interceptors;

/// <summary>
/// EF Core interceptor that automatically sets UpdatedAt timestamp on modified TodoItem entities.
/// Ensures consistent timestamp tracking for delta sync without manual intervention.
/// </summary>
public class UpdatedAtInterceptor : SaveChangesInterceptor
{
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is not null)
        {
            UpdateTimestamps(eventData.Context);
        }

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        if (eventData.Context is not null)
        {
            UpdateTimestamps(eventData.Context);
        }

        return base.SavingChanges(eventData, result);
    }

    private static void UpdateTimestamps(DbContext context)
    {
        var entries = context.ChangeTracker.Entries<TodoItem>()
            .Where(e => e.State == EntityState.Modified || e.State == EntityState.Added);

        var now = DateTime.UtcNow;

        foreach (var entry in entries)
        {
            entry.Entity.UpdatedAt = now;
        }
    }
}
