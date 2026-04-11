# CryptoSyncContextBase — Current Schema Snapshot

**Purpose**: a single self-contained document describing every table,
column, relationship, and convention baked into `CryptoSyncContextBase`
today. Intended for an external design agent doing a naming review — no
codebase navigation required. Every fact is sourced from
`SqliteWasmBlazor.CryptoSync/**` at HEAD; file paths are included inline.

---

## 1. Big picture

`CryptoSyncContextBase` is an abstract EF Core `DbContext` that domain
applications inherit from. It provides all the tables required to run the
end-to-end encrypted delta sync pipeline, plus local-only device state.
Derived contexts add their own domain `DbSet`s. The source generator
(`SqliteWasmBlazor.CryptoSync.Generator`) walks entities reachable from
the derived context and emits:

- One `Crypto_<Entity>` shadow table (C# + EF config) per syncable entity.
- A `ConfigureCryptoTables(ModelBuilder)` partial method on the derived context.
- `CryptoTableRegistry.Tables[]`, `SystemTableRegistry.Tables[]`, `SensitiveEntityRegistry.Tables[]` static lookup arrays.
- A `_column_registry` `HasData` seed pass covering every column of every syncable entity.
- A `SeedPermissions(ModelBuilder)` partial method that seeds resolved `SyncPermission` rows from `[Permissions]` / `[AllowUpdate]` / `[DenyUpdate]` attributes.

**Key invariant**: every syncable entity inherits from `SyncableEntity`,
and every syncable entity gets an encrypted shadow `_crypto_<TableName>`
that is the sync source of truth. The open table is the plaintext mirror.

Source file: `SqliteWasmBlazor.CryptoSync/CryptoSyncContextBase.cs`.

---

## 2. Base class every syncable entity inherits from

`SyncableEntity` — `SqliteWasmBlazor.CryptoSync/Models/ISyncableEntity.cs`

```csharp
public abstract class SyncableEntity
{
    public Guid       Id           { get; set; }
    public SharingScope SharingScope { get; set; }   // 0=Public, 1=Shared, 2=Client
    public string     SharingId    { get; set; } = ""; // routing key into ShareGroups
    public DateTime   UpdatedAt    { get; set; }
    public bool       IsDeleted    { get; set; }     // tombstone flag
    public DateTime?  DeletedAt    { get; set; }
}
```

These six columns are present on **every** syncable table. The worker
treats `SharingScope`, `SharingId`, `UpdatedAt`, `IsDeleted`, `DeletedAt`
as **sync-infrastructure columns** — see
`SqliteWasmBlazor/TypeScript/worker/crypto-ops.ts:380`
(`const syncColumns = new Set(['UpdatedAt', 'IsDeleted', 'DeletedAt', 'SharingScope', 'SharingId'])`).
They are excluded from column-level permission checks.

`SharingScope` enum (same file):

```csharp
public enum SharingScope
{
    Public = 0,   // encrypted, all verified contacts get the scope key
    Shared = 1,   // encrypted, only selected contacts get the scope key
    Client = 2    // encrypted, only this client's key
}
```

---

## 3. DbSets exposed on `CryptoSyncContextBase`

```csharp
public DbSet<TrustedContact>      Contacts       { get; }  // [SystemTable]
public DbSet<ShareGroup>          ShareGroups    { get; }  // [SystemTable]
public DbSet<ShareTarget>         ShareTargets   { get; }  // [SystemTable]
public DbSet<SyncPermission>      Permissions    { get; }  // local-only, not [SystemTable]
public DbSet<ColumnRegistryEntry> ColumnRegistry { get; }  // local-only, table="_column_registry"
public DbSet<SyncState>           SyncStates     { get; }  // local-only
public DbSet<DeviceSettings>      DeviceSettings { get; }  // local-only
```

Local-only tables (`Permissions`, `ColumnRegistry`, `SyncStates`,
`DeviceSettings`) never get a `_crypto_*` shadow and never ride the sync
wire. They are compile-time-static (seeded via `HasData`) or device-local
runtime state.

---

## 4. Synced system tables

### 4.1 `TrustedContact` (open table `Contacts`)

Source: `SqliteWasmBlazor.CryptoSync/Models/TrustedContact.cs`. Marked `[SystemTable]`.

| Column | Type | Notes |
|---|---|---|
| `Id` | `Guid` | inherited, PK |
| `SharingScope` | enum int | inherited |
| `SharingId` | string(128) | inherited |
| `UpdatedAt` | DateTime | inherited |
| `IsDeleted` | bool | inherited |
| `DeletedAt` | DateTime? | inherited |
| `Username` | string(128) | `[Required]` |
| `Email` | string(256) | `[Required]` |
| `Comment` | string(512)? | nullable |
| `X25519PublicKey` | string(64) | Base64, unique index, `[Required]` |
| `Ed25519PublicKey` | string(64) | Base64, unique index, `[Required]` |
| `IsAdmin` | bool | true on instance creator |
| `IsTrusted` | bool | false = pending invitation, true = active |

Config in `OnModelCreating`:
- `HasKey(Id)`
- `HasIndex(Ed25519PublicKey).IsUnique()`
- `HasIndex(X25519PublicKey).IsUnique()`
- `HasQueryFilter(e => !e.IsDeleted)` — EF filters out tombstones by default

**Role**: the identity of every participant. An untrusted contact
(`IsTrusted=false`) IS the pending invitation — no separate invite table.

### 4.2 `ShareGroup` (open table `ShareGroups`)

Source: `SqliteWasmBlazor.CryptoSync/Models/ShareGroup.cs`. Marked `[SystemTable]`.

| Column | Type | Notes |
|---|---|---|
| `Id` | Guid | inherited, PK |
| `SharingScope` | enum int | inherited |
| `SharingId` | string(128) | inherited |
| `UpdatedAt` | DateTime | inherited |
| `IsDeleted` | bool | inherited |
| `DeletedAt` | DateTime? | inherited |
| `GroupContext` | string(256) | `[Required]`, unique index. HKDF info param. Example: `"system:v1"`, `"group-abc:v1"`. Bound as AAD during encryption. |
| `KeyVersion` | int | Incremented on rotation (member removal). Old versions retained in ShareTargets. |
| `AdminPublicKey` | string(64) | `[Required]`, Base64 X25519. ECDH counterparty for CEK unwrap. |
| `CreatedAt` | DateTime | row creation time |

Config:
- `HasKey(Id)`
- `HasIndex(GroupContext).IsUnique()`
- `HasQueryFilter(e => !e.IsDeleted)`

**Role**: one row per sharing group, including "self" groups (single
member). The admin of the group is whoever holds `ShareTarget.Role = Owner`
for this group.

### 4.3 `ShareTarget` (open table `ShareTargets`)

Source: `SqliteWasmBlazor.CryptoSync/Models/ShareTarget.cs`. Marked `[SystemTable]`.

| Column | Type | Notes |
|---|---|---|
| `Id` | Guid | inherited, PK |
| `SharingScope` | enum int | inherited |
| `SharingId` | string(128) | inherited |
| `UpdatedAt` | DateTime | inherited |
| `IsDeleted` | bool | inherited |
| `DeletedAt` | DateTime? | inherited |
| `ShareGroupId` | Guid | FK → `ShareGroup.Id`, cascade delete |
| `ShareGroup` | nav | back-ref |
| `KeyVersion` | int | matches `ShareGroup.KeyVersion` for current messages |
| `MemberPublicKey` | string(64) | `[Required]`, Base64 X25519. Lookup key — "which groups can I decrypt?" |
| `WrappedContentKey` | byte[] | `[Required]`. `[nonce(12)|ciphertext]` AES-GCM blob. Encrypted with the HKDF-derived wrapping key. |
| `Role` | `SyncRole` enum | 0=Owner, 1=Editor, 2=Viewer |
| `GrantedByContactId` | Guid | FK → `TrustedContact.Id`, restrict delete |
| `GrantedByContact` | nav | back-ref |

Config:
- `HasKey(Id)`
- `HasIndex(ShareGroupId, KeyVersion, MemberPublicKey).IsUnique()` — composite
- `HasIndex(MemberPublicKey)` — non-unique, lookup index
- `HasOne(ShareGroup).WithMany().HasForeignKey(ShareGroupId).OnDelete(Cascade)`
- `HasOne(GrantedByContact).WithMany().HasForeignKey(GrantedByContactId).OnDelete(Restrict)`
- `HasQueryFilter(e => !e.IsDeleted)`

**Role**: per-member wrapped CEK. One row per (group × keyVersion × member).
Removing a member = delete the row for the NEW key version only; old
versions stay so prior messages remain decryptable.

`SyncRole` enum — `SqliteWasmBlazor.CryptoSync/Models/Enums.cs`:

```csharp
public enum SyncRole { Owner = 0, Editor = 1, Viewer = 2 }
```

---

## 5. Local-only, non-synced tables

### 5.1 `SyncPermission` (open table `Permissions`)

Source: `SqliteWasmBlazor.CryptoSync/Models/SyncPermission.cs`. **Not** a
`[SystemTable]`. Does NOT inherit `SyncableEntity` but carries the same
infrastructure columns inline so the generator can treat it uniformly in
seed code.

| Column | Type | Notes |
|---|---|---|
| `Id` | Guid | PK |
| `SharingScope` | enum int | present but always `Public` for seeds |
| `SharingId` | string | present but always `"system"` for seeds |
| `UpdatedAt` | DateTime | |
| `IsDeleted` | bool | |
| `DeletedAt` | DateTime? | |
| `Role` | `SyncRole` | 0=Owner, 1=Editor, 2=Viewer |
| `TableName` | string(128) | `[Required]` — which table this permission applies to |
| `RecordId` | Guid? | NULL = table-wide rule; non-null = per-row override |
| `CanInsert` | bool | resolved CRUD flag |
| `CanRead` | bool | |
| `CanUpdate` | bool | |
| `CanDelete` | bool | |
| `ReadonlyColumns` | string(2048) | CSV of column names this role may NOT update (even if `CanUpdate=true`) |
| `ReadwriteColumns` | string(2048) | CSV of column names this role MAY update (even if `CanUpdate=false`) |

Config:
- `HasKey(Id)`
- `HasIndex(SharingScope, SharingId, TableName, RecordId, Role).IsUnique()` — composite
- `HasQueryFilter(e => !e.IsDeleted)`

**Seeded data**:
- The base context's `SeedSystemTablePermissions` seeds Owner/Editor/Viewer rules for `Contacts`, `ShareGroups`, `ShareTargets` — Owner has full CRUD, Editor/Viewer are read-only.
- The generator seeds additional rows for every domain entity from its `[Permissions]` / `[AllowUpdate]` / `[DenyUpdate]` attributes.
- Lookup order at import time: `(Table, RecordId=thisRow.Id)` first, fall back to `(Table, RecordId=NULL)`.

### 5.2 `ColumnRegistryEntry` (open table `_column_registry`)

Source: `SqliteWasmBlazor.CryptoSync/Models/ColumnRegistry.cs`.

| Column | Type | Notes |
|---|---|---|
| `Id` | Guid | deterministic (`TableName + ColumnIndex`) |
| `TableName` | string(128) | `[Required]` — open table name |
| `ColumnIndex` | int | 0-based position in export order |
| `ColumnName` | string(128) | `[Required]` — matches entity property name |
| `SqlType` | string(16) | `[Required]` — `TEXT`/`INTEGER`/`REAL`/`BLOB` |
| `CSharpType` | string(64) | `[Required]` — `Guid`, `String`, `Int32`, `DateTime`, etc. |
| `IsPrimaryKey` | bool | true for the `Id` column only |

Config:
- `ToTable("_column_registry")` — literal lowercase underscore name
- `HasKey(Id)`
- `HasIndex(TableName, ColumnIndex).IsUnique()`

**Role**: schema SSOT the worker reads at import time. Seeded via
`HasData` by `ConfigureCryptoTables(modelBuilder)` (generator output).
Identical on every client — not synced, never changes at runtime.

### 5.3 `SyncState` (open table `SyncStates`)

Source: `SqliteWasmBlazor.CryptoSync/Models/SyncState.cs`.

| Column | Type | Notes |
|---|---|---|
| `Id` | Guid | PK |
| `LastSyncAt` | DateTime | monotonic delta cursor. `DateTime.MinValue` = never synced |
| `LastDeltaHash` | string? | reserved, not yet used |

Config: `HasKey(Id)`.

**Role**: local delta cursor. `SyncOrchestrator.ExportAsync` reads it,
calls the worker with `sinceTimestamp`, advances it on success.

### 5.4 `DeviceSettings` (open table `DeviceSettings`)

Source: `SqliteWasmBlazor.CryptoSync/Models/DeviceSettings.cs`.

| Column | Type | Notes |
|---|---|---|
| `Id` | Guid | PK |
| `ClientGuid` | string(64) | `[Required]` — stable device identifier |
| `DeviceName` | string(128) | `[Required]` — human-readable label |
| `CredentialId` | string(256)? | WebAuthn credential ID hint for auto-fill |
| `IsAdmin` | bool | true on the instance-creator device |
| `AdminContactId` | Guid? | on non-admin devices: resolved `TrustedContact.Id` of the admin |

Config: `HasKey(Id)`. Local-only, never synced.

---

## 6. Generator-emitted crypto shadow tables

For every syncable entity `X` (including `[SystemTable]` ones), the
generator emits a `Crypto_X` class mapped to `_crypto_<OpenTableName>`.

Example shape (`Crypto_CryptoTestList` — generator output):

```csharp
public sealed class Crypto_CryptoTestList
{
    public Guid    Id                { get; set; }   // matches open-table PK
    public int     SharingScope      { get; set; }   // plaintext, routing
    public string  SharingId         { get; init; }  // plaintext, routing (128)
    public byte[]  EncryptedRow      { get; init; }  // AES-GCM ciphertext over ALL columns
    public byte[]  Nonce             { get; init; }  // per-row 12-byte AES-GCM nonce
    public int     KeyVersion        { get; set; }   // selects CEK version — Layer 1 AAD
    public string  SenderPublicKey   { get; set; }   // Ed25519 hex, Layer 2 verification (64)
    public byte[]  EnvelopeSignature { get; set; }   // per-row Ed25519 sig (legacy; batch sig now at group level)
}
```

Generator-emitted EF config (per shadow):

```csharp
e.ToTable("_crypto_<OpenTableName>");
e.HasKey(x => x.Id);
e.HasIndex(x => x.SharingId);
e.HasIndex(x => x.SharingScope);
```

**Naming convention today**:
- Domain entity `Foo` → open DbSet `Foos` (or whatever the derived context calls it) → shadow class `Crypto_Foo` → shadow table `_crypto_Foos`.
- Underscore separator in class name (`Crypto_Foo`), lowercase prefix in table name (`_crypto_Foos`).

Shadow classes live in the derived context's namespace, not
`SqliteWasmBlazor.CryptoSync` — so domain apps see `Crypto_*` types next
to their domain entities.

---

## 7. Generator-emitted static registries

All three live in the derived context's namespace.

### 7.1 `CryptoTableRegistry`

```csharp
public static class CryptoTableRegistry
{
    public static readonly (string EntityName, string CryptoTableName, string OpenTableName)[] Tables = [ ... ];
}
```

Every syncable entity — system + domain + sensitive — one row each.

### 7.2 `SystemTableRegistry`

```csharp
public static class SystemTableRegistry
{
    public static readonly (string EntityName, string TableName)[] Tables = [ ... ];
    public static bool IsSystem(string tableName);
}
```

Only entities with `[SystemTable]`. Used by:
- Staggered import ordering in the worker (system-first).
- Future ownership-transfer refusal (system scopes cannot be transferred off the admin device — aspirational, no enforcement yet).

### 7.3 `SensitiveEntityRegistry`

```csharp
public static class SensitiveEntityRegistry
{
    public static readonly (string EntityName, string TableName)[] Tables = [ ... ];
    public static bool IsSensitive(string tableName);
}
```

Entities marked `[Sensitive]` — shadow-only, never in a plaintext open
table. Accessed via a future `SensitiveAccessService` (not implemented
yet).

---

## 8. Attributes the generator consumes

Source: `SqliteWasmBlazor.CryptoSync/Attributes/`.

### 8.1 `[SystemTable]`

```csharp
[AttributeUsage(AttributeTargets.Class)]
public sealed class SystemTableAttribute : Attribute;
```

Marks an entity as admin-managed system metadata. Consumed by the
generator to populate `SystemTableRegistry` and gate worker-side admin
verification on import (`verifySenderIsAdmin` in `crypto-ops.ts`).

### 8.2 `[Sensitive]`

```csharp
[AttributeUsage(AttributeTargets.Class)]
public sealed class SensitiveAttribute : Attribute;
```

Shadow-only, no plaintext open table.

### 8.3 `[Permissions]` (multi-instance)

```csharp
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class PermissionsAttribute(string defaultRole = "Any") : Attribute
{
    public string Create { get; set; }   // role that can insert
    public string Read   { get; set; }   // role that can read
    public string Update { get; set; }   // role that can update
    public string Delete { get; set; }   // role that can delete
    public const string Any = "Any";     // wildcard
}
```

Declarative CRUD policy per entity. Example:
`[Permissions("Editor", Delete = "Owner")]` = Editor has full CRUD except
only Owner can delete. The generator resolves stacking at compile time
and seeds `SyncPermission` rows with fully-resolved `CanInsert` /
`CanRead` / `CanUpdate` / `CanDelete` bool flags per role.

### 8.4 `[AllowUpdate("role[,role,…]")]`

```csharp
[AttributeUsage(AttributeTargets.Property)]
public sealed class AllowUpdateAttribute(string roles) : Attribute;
```

Per-column override — listed roles may update this column even when
table-level `CanUpdate` denies. Example:

```csharp
[AllowUpdate("Viewer")]
public bool IsBought { get; set; }
```

Viewer cannot update anything at the table level, but can flip this one
bool.

### 8.5 `[DenyUpdate("role[,role,…]")]`

```csharp
[AttributeUsage(AttributeTargets.Property)]
public sealed class DenyUpdateAttribute(string roles) : Attribute;
```

Inverse — listed roles cannot update this column even when `CanUpdate`
allows.

### 8.6 `[Share("fromRole", "toRole")]` (multi-instance)

```csharp
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class ShareAttribute(string fromRole, string toRole) : Attribute;
```

Declared share capability: which existing role may grant which new role
on this entity. Example: `[Share("Owner", "Editor")]` = only Owner can
share as Editor. **Currently declarative metadata only** — no runtime
enforcement by the existing library. Provided for the future
SharingService.

### 8.7 `[ShareLabel]`

```csharp
[AttributeUsage(AttributeTargets.Property)]
public sealed class ShareLabelAttribute : Attribute;
```

Marks a property (e.g. list name) as the display label when presenting a
share UI.

### 8.8 `[InheritPermissions("parentTableName")]`

```csharp
[AttributeUsage(AttributeTargets.Class)]
public sealed class InheritPermissionsAttribute(string table) : Attribute;
```

Child entity reuses parent's CRUD permission rules. Example:
`[InheritPermissions("ShoppingLists")]` on `ShoppingItem` — items inherit
list permissions.

### 8.9 Role strings

The wildcard is the literal string `"Any"` (see `PermissionsAttribute.Any`).
Role strings in attributes must match `SyncRole` enum names: `"Owner"`,
`"Editor"`, `"Viewer"`.

---

## 9. Bootstrap seed layout

Source generator project: `SqliteWasmBlazor.AdminSeed`. Emits a
`<ContextName>.AdminSeed.g.cs` partial method
`SeedAdminBootstrap(ModelBuilder)` on the derived context. What it seeds:

- **Admin `TrustedContact`** — `Username="TestAdmin"` etc., `IsAdmin=true`,
  `IsTrusted=true`, `SharingScope=Public`, `SharingId="system"`. Fixed
  deterministic Guid.
- **System `ShareGroup`** — `GroupContext="system:v1"`, `KeyVersion=1`,
  `AdminPublicKey=<admin X25519 pub>`, `SharingId="system"`.
- **Admin `ShareTarget`** — `ShareGroupId=<system group>`, `KeyVersion=1`,
  `MemberPublicKey=<admin X25519 pub>`, `WrappedContentKey=<pre-baked wrapped CEK>`,
  `Role=Owner`, `GrantedByContactId=<admin contact id>`, `SharingId="system"`.
- **Admin `DeviceSettings`** — `IsAdmin=true`, `AdminContactId=<admin contact id>`.

Well-known constants:
- `CryptoSyncBootstrap.SystemGroupContext = "system:v1"` — the canonical HKDF info string for the system group.
- `CryptoSyncBootstrap.SystemSharingId = "system"` — the canonical SharingId every system-scope row uses.

All other rows initially live in `SharingId = "system"` (Public scope) —
nothing is "locked down" to a private group by default.

---

## 10. Composition contract for a derived context

A domain app's DbContext typically looks like:

```csharp
public partial class MyAppContext : CryptoSyncContextBase
{
    public MyAppContext(DbContextOptions<MyAppContext> options) : base(options) { }

    public DbSet<MyEntity>      MyEntities      { get; } = null!;
    public DbSet<MyOtherEntity> MyOtherEntities { get; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);   // required — seeds system permissions

        // Domain FK configuration
        modelBuilder.Entity<MyOtherEntity>()
            .HasOne(o => o.Parent).WithMany(p => p.Children)
            .HasForeignKey(o => o.ParentId)
            .OnDelete(DeleteBehavior.Restrict);

        ConfigureCryptoTables(modelBuilder);  // generator partial — shadow tables + _column_registry seed
        SeedPermissions(modelBuilder);        // generator partial — domain entity permissions
        SeedAdminBootstrap(modelBuilder);     // AdminSeed-generator partial — admin + system group
    }
}
```

The three generator-emitted `Seed*` / `Configure*` calls **must** run in
`OnModelCreating` in this order. Missing any one breaks the pipeline.

---

## 11. Naming surface a design agent may want to revisit

**DbSet property names on base context**:
- `Contacts` (table: `Contacts`) — `DbSet<TrustedContact>`
- `ShareGroups` (table: `ShareGroups`) — `DbSet<ShareGroup>`
- `ShareTargets` (table: `ShareTargets`) — `DbSet<ShareTarget>`
- `Permissions` (table: `Permissions`) — `DbSet<SyncPermission>`
- `ColumnRegistry` (table: `_column_registry`) — `DbSet<ColumnRegistryEntry>`
- `SyncStates` (table: `SyncStates`) — `DbSet<SyncState>`
- `DeviceSettings` (table: `DeviceSettings`) — `DbSet<DeviceSettings>` (plural/singular inconsistency — DbSet name equals entity name)

**Entity class names**:
- `SyncableEntity` (abstract base)
- `TrustedContact`, `ShareGroup`, `ShareTarget`, `SyncPermission`
- `ColumnRegistryEntry`, `SyncState`, `DeviceSettings`

**Enum names**:
- `SharingScope { Public = 0, Shared = 1, Client = 2 }`
- `SyncRole { Owner = 0, Editor = 1, Viewer = 2 }`
- `SyncOperation { Insert = 0, Update = 1, Delete = 2 }` (worker-derived, not stored)

**Column names repeated across entities** (from `SyncableEntity`):
- `Id`, `SharingScope`, `SharingId`, `UpdatedAt`, `IsDeleted`, `DeletedAt`

**System-table-specific columns**:
- `TrustedContact`: `Username`, `Email`, `Comment`, `X25519PublicKey`, `Ed25519PublicKey`, `IsAdmin`, `IsTrusted`
- `ShareGroup`: `GroupContext`, `KeyVersion`, `AdminPublicKey`, `CreatedAt`
- `ShareTarget`: `ShareGroupId`, `KeyVersion`, `MemberPublicKey`, `WrappedContentKey`, `Role`, `GrantedByContactId`
- `SyncPermission`: `Role`, `TableName`, `RecordId`, `CanInsert` / `CanRead` / `CanUpdate` / `CanDelete`, `ReadonlyColumns`, `ReadwriteColumns`
- `DeviceSettings`: `ClientGuid`, `DeviceName`, `CredentialId`, `IsAdmin`, `AdminContactId`
- `SyncState`: `LastSyncAt`, `LastDeltaHash`

**Shadow table naming**:
- Class: `Crypto_<EntityName>` (underscore separator)
- SQL table: `_crypto_<OpenTableName>` (leading underscore + lowercase prefix)
- Internal columns: `Id`, `SharingScope`, `SharingId`, `EncryptedRow`, `Nonce`, `KeyVersion`, `SenderPublicKey`, `EnvelopeSignature`

**Constant strings**:
- `SystemGroupContext = "system:v1"` — HKDF info / AAD binding
- `SystemSharingId = "system"` — routing key
- `PermissionsAttribute.Any = "Any"` — wildcard role

**Attribute names**:
- `[SystemTable]`, `[Sensitive]`, `[Permissions]`, `[AllowUpdate]`, `[DenyUpdate]`, `[Share]`, `[ShareLabel]`, `[InheritPermissions]`

**Generator artifact names**:
- `CryptoTableRegistry`, `SystemTableRegistry`, `SensitiveEntityRegistry`
- `ConfigureCryptoTables(modelBuilder)`, `SeedPermissions(modelBuilder)`, `SeedAdminBootstrap(modelBuilder)`

---

## 12. File index

| File | What's in it |
|---|---|
| `SqliteWasmBlazor.CryptoSync/CryptoSyncContextBase.cs` | Base context, DbSets, OnModelCreating, system-permission seed |
| `SqliteWasmBlazor.CryptoSync/Models/ISyncableEntity.cs` | `SyncableEntity` base + `SharingScope` enum |
| `SqliteWasmBlazor.CryptoSync/Models/TrustedContact.cs` | Contact entity |
| `SqliteWasmBlazor.CryptoSync/Models/ShareGroup.cs` | Group metadata |
| `SqliteWasmBlazor.CryptoSync/Models/ShareTarget.cs` | Per-member wrapped CEK |
| `SqliteWasmBlazor.CryptoSync/Models/SyncPermission.cs` | Resolved CRUD + column permissions |
| `SqliteWasmBlazor.CryptoSync/Models/ColumnRegistry.cs` | Schema metadata (`_column_registry`) |
| `SqliteWasmBlazor.CryptoSync/Models/SyncState.cs` | Delta cursor |
| `SqliteWasmBlazor.CryptoSync/Models/DeviceSettings.cs` | Local device identity |
| `SqliteWasmBlazor.CryptoSync/Models/Enums.cs` | `SyncRole`, `SyncOperation` |
| `SqliteWasmBlazor.CryptoSync/Attributes/SystemTableAttribute.cs` | `[SystemTable]` |
| `SqliteWasmBlazor.CryptoSync/Attributes/SensitiveAttribute.cs` | `[Sensitive]` |
| `SqliteWasmBlazor.CryptoSync/Attributes/SyncPermissionAttribute.cs` | `[Permissions]`, `[AllowUpdate]`, `[DenyUpdate]`, `[Share]`, `[ShareLabel]`, `[InheritPermissions]` |
| `SqliteWasmBlazor.CryptoSync/Services/CryptoSyncBootstrap.cs` | Admin seed runtime helper + `SystemGroupContext` / `SystemSharingId` constants |
| `SqliteWasmBlazor.CryptoSync.Generator/CryptoSyncGenerator.cs` | Generates `Crypto_*` classes, `ConfigureCryptoTables`, `SeedPermissions`, registries, and `_column_registry` HasData |

End of document.
