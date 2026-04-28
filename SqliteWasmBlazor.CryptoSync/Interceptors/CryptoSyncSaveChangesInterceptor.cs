using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;

namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// Auto-populates sync metadata on every <see cref="SyncableEntity"/> save:
///
/// <list type="bullet">
///   <item>
///     <b>Added</b> — assigns <see cref="SyncableEntity.Id"/> if empty,
///     stamps <see cref="SyncableEntity.UpdatedAt"/>, and resolves
///     <see cref="SyncableEntity.SharingId"/> via a four-step cascade:
///     system-table short-circuit → caller-explicit → parent inheritance
///     via <see cref="InheritPermissionsAttribute"/> → scope-driven default
///     (<see cref="SharingScope.CLIENT"/> = self-group,
///     <see cref="SharingScope.PUBLIC"/> = system, <see cref="SharingScope.SHARED"/>
///     requires explicit caller-supplied SharingId).
///   </item>
///   <item>
///     <b>Modified</b> — bumps <see cref="SyncableEntity.UpdatedAt"/> and
///     enforces the immutability invariant: throws
///     <see cref="InvalidOperationException"/> if a Modified entry rewrote
///     <see cref="SyncableEntity.SharingId"/> or
///     <see cref="SyncableEntity.SharingScope"/>. Developer safety net —
///     not a security boundary, just a clear error pointing at "create a
///     fresh row in the target group instead".
///   </item>
///   <item>
///     <b>Deleted</b> — converts hard delete into soft delete (sets
///     <see cref="SyncableEntity.IsDeleted"/>, <see cref="SyncableEntity.DeletedAt"/>,
///     bumps UpdatedAt) and cascades the soft-delete to every FK descendant
///     via <see cref="SyncableFkCascade.SoftDeleteSubtreeAsync"/>.
///   </item>
/// </list>
///
/// Registered automatically on every <see cref="CryptoSyncContextBase"/>
/// derivative via <see cref="CryptoSyncContextBase.OnConfiguring"/>.
/// </summary>
public sealed class CryptoSyncSaveChangesInterceptor : SaveChangesInterceptor
{
    // Process-wide attribute lookup caches. Probed via Type.GetCustomAttribute,
    // which is the same shape the existing generator-emitted code uses.
    private static readonly ConcurrentDictionary<Type, bool> SystemTableCache = new();
    private static readonly ConcurrentDictionary<Type, string?> InheritParentCache = new();

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is CryptoSyncContextBase ctx)
        {
            await ApplyLifecycleAsync(ctx, cancellationToken).ConfigureAwait(false);
        }
        return await base.SavingChangesAsync(eventData, result, cancellationToken).ConfigureAwait(false);
    }

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        if (eventData.Context is CryptoSyncContextBase ctx)
        {
            // Sync wrapper — same lifecycle pass synchronously. The cascade
            // helper is async, so we block on it (callers using sync
            // SaveChanges accept the trade-off).
            ApplyLifecycleAsync(ctx, CancellationToken.None).GetAwaiter().GetResult();
        }
        return base.SavingChanges(eventData, result);
    }

    private static async Task ApplyLifecycleAsync(CryptoSyncContextBase ctx, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        Guid? cachedOwnContactId = null;
        var ownContactIdResolved = false;

        // Snapshot — soft-delete cascade may add new tracked entries, and we
        // don't want to revisit them in the same pass.
        var entries = ctx.ChangeTracker
            .Entries<SyncableEntity>()
            .ToList();

        foreach (var entry in entries)
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    ApplyAddedAsync(entry, ctx, now, ref cachedOwnContactId, ref ownContactIdResolved);
                    break;

                case EntityState.Modified:
                    await ApplyModifiedAsync(entry, ctx, now, ct).ConfigureAwait(false);
                    break;

                case EntityState.Deleted:
                    await ApplyDeletedAsync(entry, ctx, now, ct).ConfigureAwait(false);
                    break;
            }
        }
    }

    // ------------------------------------------------------------------
    // Added — assign defaults + scope-driven SharingId routing
    // ------------------------------------------------------------------

    private static void ApplyAddedAsync(
        EntityEntry<SyncableEntity> entry,
        CryptoSyncContextBase ctx,
        DateTime now,
        ref Guid? cachedOwnContactId,
        ref bool ownContactIdResolved)
    {
        var entity = entry.Entity;

        if (entity.Id == Guid.Empty)
        {
            entity.Id = Guid.NewGuid();
        }

        entity.UpdatedAt = now;

        // STEP 1 — system-table short-circuit. [SystemTable] entities default
        // to riding the system CEK regardless of SharingScope. This closes
        // the circular dep where a self-group's own ShareGroup row would
        // otherwise need the self-CEK to encrypt its own transport shadow.
        // Caller may opt out by pre-assigning SharingId before SaveChanges
        // (Invitation rows ride the invitation share group's CEK, so they
        // need that override).
        var clrType = entity.GetType();
        if (HasSystemTableAttribute(clrType))
        {
            // Defensive: [SystemTable] + [InheritPermissions] would be a
            // declaration bug — they're mutually exclusive by construction.
            if (HasInheritPermissionsAttribute(clrType))
            {
                throw new InvalidOperationException(
                    $"CryptoSyncSaveChangesInterceptor: entity {clrType.Name} carries both " +
                    $"[SystemTable] and [InheritPermissions]. These attributes are mutually " +
                    $"exclusive — system tables route via the system CEK, parent-inherited " +
                    $"entities route via their parent's group.");
            }
            if (string.IsNullOrEmpty(entity.SharingId))
            {
                entity.SharingId = CryptoSyncBootstrap.SystemSharingId;
            }
            return;
        }

        // STEP 2 — caller-explicit wins.
        if (!string.IsNullOrEmpty(entity.SharingId))
        {
            return;
        }

        // STEP 3 — parent inheritance via [InheritPermissions("parentTable")].
        var parentTable = GetInheritParent(clrType);
        if (parentTable is not null && TryCopyFromParent(entry, ctx, parentTable))
        {
            return;
        }

        // STEP 4 — scope-driven default. Default scope = Client (private).
        switch (entity.SharingScope)
        {
            case SharingScope.CLIENT:
            {
                if (!ownContactIdResolved)
                {
                    cachedOwnContactId = ctx.DeviceSettings
                        .AsNoTracking()
                        .Select(d => d.OwnContactId)
                        .FirstOrDefault();
                    ownContactIdResolved = true;
                }
                if (cachedOwnContactId is not { } ownContactId || ownContactId == Guid.Empty)
                {
                    throw new InvalidOperationException(
                        "CryptoSyncSaveChangesInterceptor: cannot route a Client-scoped " +
                        "entity to a self-group because DeviceSettings.OwnContactId is null. " +
                        "Call DeviceIdentityService.SetOwnContactIdAsync during device bootstrap " +
                        "(or rely on the admin bootstrap, which sets it automatically).");
                }
                entity.SharingId = CryptoSyncBootstrap.BuildSelfGroupContext(ownContactId);
                break;
            }
            case SharingScope.PUBLIC:
                entity.SharingId = CryptoSyncBootstrap.SystemSharingId;
                break;
            case SharingScope.SHARED:
                throw new InvalidOperationException(
                    "CryptoSyncSaveChangesInterceptor: SharingScope.Shared requires an " +
                    "explicit SharingId — assign the target ShareGroup's GroupContext to " +
                    "entity.SharingId before calling SaveChanges. The interceptor cannot " +
                    "infer which shared group a row belongs to.");
            default:
                throw new InvalidOperationException(
                    $"CryptoSyncSaveChangesInterceptor: unknown SharingScope {(int)entity.SharingScope}");
        }
    }

    private static bool TryCopyFromParent(
        EntityEntry<SyncableEntity> entry,
        CryptoSyncContextBase ctx,
        string parentTableName)
    {
        // Find the FK property on this entity that targets the parent table.
        var entityType = entry.Metadata;
        IForeignKey? matchingFk = null;
        foreach (var fk in entityType.GetForeignKeys())
        {
            if (string.Equals(fk.PrincipalEntityType.GetTableName(), parentTableName, StringComparison.Ordinal))
            {
                matchingFk = fk;
                break;
            }
        }
        if (matchingFk is null || matchingFk.Properties.Count != 1)
        {
            return false;
        }

        // Pull the FK value off the tracked entry.
        var fkProp = matchingFk.Properties[0];
        var fkValue = entry.Property(fkProp.Name).CurrentValue;
        if (fkValue is not Guid parentId || parentId == Guid.Empty)
        {
            return false;
        }

        // Look up the parent row. Prefer tracked instances (cheap, no DB hit);
        // fall back to a one-shot raw SQL read of (SharingScope, SharingId).
        var parentClrType = matchingFk.PrincipalEntityType.ClrType;
        SyncableEntity? trackedParent = null;
        foreach (var pe in ctx.ChangeTracker.Entries<SyncableEntity>())
        {
            if (parentClrType.IsInstanceOfType(pe.Entity) && pe.Entity.Id == parentId)
            {
                trackedParent = pe.Entity;
                break;
            }
        }
        if (trackedParent is not null)
        {
            entry.Entity.SharingScope = trackedParent.SharingScope;
            entry.Entity.SharingId = trackedParent.SharingId;
            return !string.IsNullOrEmpty(trackedParent.SharingId);
        }

        // Fallback — raw SQL fetch from the open table. EF1002 suppressed:
        // identifiers come from EF model metadata.
#pragma warning disable EF1002
        var rows = ctx.Database
            .SqlQueryRaw<ParentScopeRow>(
                $"SELECT \"SharingScope\" AS Scope, \"SharingId\" AS SharingId FROM \"{parentTableName}\" WHERE \"Id\" = {{0}} LIMIT 1",
                parentId)
            .ToList();
#pragma warning restore EF1002
        if (rows.Count == 0)
        {
            return false;
        }
        entry.Entity.SharingScope = (SharingScope)rows[0].Scope;
        entry.Entity.SharingId = rows[0].SharingId;
        return !string.IsNullOrEmpty(rows[0].SharingId);
    }

    // Used by SqlQueryRaw — projection record. Internal so EF can map columns.
    private sealed record ParentScopeRow(int Scope, string SharingId);

    // ------------------------------------------------------------------
    // Modified — bump UpdatedAt + immutability guard + IsDeleted cascade
    // ------------------------------------------------------------------

    private static async Task ApplyModifiedAsync(
        EntityEntry<SyncableEntity> entry,
        CryptoSyncContextBase ctx,
        DateTime now,
        CancellationToken ct)
    {
        // DEVELOPER SAFETY GUARD: SharingId / SharingScope are write-once
        // per row. Reject any save that mutates them.
        var sharingIdProp = entry.Property(nameof(SyncableEntity.SharingId));
        var sharingScopeProp = entry.Property(nameof(SyncableEntity.SharingScope));
        if (sharingIdProp.IsModified || sharingScopeProp.IsModified)
        {
            throw new InvalidOperationException(
                "CryptoSyncSaveChangesInterceptor: SharingId and SharingScope are immutable " +
                "after row creation. To move a record between groups, create a fresh row " +
                "with a new Id in the target group instead of mutating SharingId in place. " +
                "Cross-group transfers will be supported by a future TransferService.");
        }

        entry.Entity.UpdatedAt = now;

        // Soft-delete via property mutation: when the caller flips
        // IsDeleted to true directly (instead of Remove()), treat the same
        // as the Deleted branch — stamp DeletedAt and cascade to FK
        // descendants. This path is the recommended pattern when FKs use
        // OnDelete.Restrict and children may be tracked — Remove() on a
        // restricted parent would trip EF's navigation-severance guard
        // before the interceptor can convert the state.
        var isDeletedProp = entry.Property(nameof(SyncableEntity.IsDeleted));
        if (isDeletedProp.IsModified && entry.Entity.IsDeleted)
        {
            if (entry.Entity.DeletedAt is null)
            {
                entry.Entity.DeletedAt = now;
            }

            var tableName = entry.Metadata.GetTableName()
                ?? throw new InvalidOperationException(
                    $"CryptoSyncSaveChangesInterceptor: entity {entry.Entity.GetType().Name} has no mapped table");

            await SyncableFkCascade
                .SoftDeleteSubtreeAsync(ctx, tableName, entry.Entity.Id, ct)
                .ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------
    // Deleted — convert to soft-delete + cascade to FK descendants
    // ------------------------------------------------------------------

    private static async Task ApplyDeletedAsync(
        EntityEntry<SyncableEntity> entry,
        CryptoSyncContextBase ctx,
        DateTime now,
        CancellationToken ct)
    {
        var entity = entry.Entity;
        var tableName = entry.Metadata.GetTableName()
            ?? throw new InvalidOperationException(
                $"CryptoSyncSaveChangesInterceptor: entity {entity.GetType().Name} has no mapped table");

        // Flip the entry from Deleted → Modified BEFORE the in-place mutation
        // so EF tracks the column updates.
        entry.State = EntityState.Modified;
        entity.IsDeleted = true;
        entity.DeletedAt = now;
        entity.UpdatedAt = now;

        // Cascade soft-delete to every FK descendant via raw SQL — keeps the
        // change tracker out of the recursion (potentially many rows). The
        // helper is idempotent: it skips rows already marked IsDeleted = 1.
        await SyncableFkCascade
            .SoftDeleteSubtreeAsync(ctx, tableName, entity.Id, ct)
            .ConfigureAwait(false);
    }

    // ------------------------------------------------------------------
    // Attribute lookup helpers (cached, AOT-safe)
    // ------------------------------------------------------------------

    private static bool HasSystemTableAttribute(Type type)
    {
        return SystemTableCache.GetOrAdd(type, static t =>
            t.GetCustomAttribute<SystemTableAttribute>(inherit: true) is not null);
    }

    private static bool HasInheritPermissionsAttribute(Type type)
        => GetInheritParent(type) is not null;

    private static string? GetInheritParent(Type type)
    {
        return InheritParentCache.GetOrAdd(type, static t =>
            t.GetCustomAttribute<InheritPermissionsAttribute>(inherit: true)?.Table);
    }
}
