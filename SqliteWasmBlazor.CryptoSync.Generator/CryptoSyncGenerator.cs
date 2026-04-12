using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SqliteWasmBlazor.CryptoSync.Generator;

[Generator]
public class CryptoSyncGenerator : IIncrementalGenerator
{
    // Wildcard role string used by PermissionsAttribute when no constraint applies.
    private const string AnyRole = "Any";

    // Standalone (non-syncable) base-context entities that never get a shadow,
    // never participate in permission seeds, and are local-only on each device.
    private static readonly HashSet<string> StandaloneBaseEntityNames = new()
    {
        "DeviceSettings", "SyncState"
    };

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Find all classes that inherit from CryptoSyncContextBase
        var contextClasses = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (s, _) => IsDbContextCandidate(s),
                transform: static (ctx, _) => (ClassDecl: (ClassDeclarationSyntax)ctx.Node, SemanticModel: ctx.SemanticModel))
            .Where(static x => x.ClassDecl is not null);

        var collected = contextClasses.Collect();

        context.RegisterSourceOutput(collected, static (spc, contexts) =>
        {
            foreach (var (classDecl, semanticModel) in contexts)
            {
                var classSymbol = semanticModel.GetDeclaredSymbol(classDecl);
                if (classSymbol is null)
                {
                    continue;
                }

                if (!InheritsFromCryptoSyncContextBase(classSymbol))
                {
                    continue;
                }

                // Every syncable entity reachable from this context — both domain entities
                // (user's own DbSets) AND system entities (TrustedContact, SyncPermission,
                // SharingKey declared in CryptoSyncContextBase). Both need crypto shadow
                // tables because both flow over the wire during sync.
                var allEntities = GetSyncableEntities(classSymbol);

                // Useful derived views for the generators below.
                var systemEntities = allEntities.Where(e => e.IsSystemTable).ToList();
                var domainEntities = allEntities.Where(e => !e.IsSystemTable).ToList();
                var sensitiveEntities = domainEntities.Where(e => e.IsSensitive).ToList();

                if (allEntities.Count == 0)
                {
                    continue;
                }

                var ns = classSymbol.ContainingNamespace.ToDisplayString();

                // Generate Crypto_<Entity> shadow class for EVERY syncable entity, including
                // system tables. The _crypto_ shadow table is the sync source of truth — it
                // holds per-row (Id, SharingScope, SharingId, EncryptedRow, Nonce). Sensitive
                // entities also get a shadow (that's where they live exclusively); their
                // suppression applies only to the open-table query path (handled later in
                // SensitiveAccessService, not here in the generator).
                foreach (var entity in allEntities)
                {
                    var source = GenerateCryptoEntity(ns, entity);
                    spc.AddSource($"Crypto_{entity.Name}.g.cs", source);
                }

                // FK relation map: static compile-time map of parent→children FK
                // relationships for AOT-safe subtree walks (soft-delete cascade,
                // future TransferService clone+remap).
                var fkRelations = DiscoverFkRelations(allEntities, classSymbol);
                var fkMapSource = GenerateSyncableFkMap(ns, fkRelations);
                spc.AddSource("SyncableFkMap.g.cs", fkMapSource);

                // ConfigureCryptoTables(ModelBuilder) partial + GetChildFkRelations override
                // — EF Core config for every shadow table the context needs, including system ones.
                var configSource = GenerateEfConfiguration(ns, classSymbol.Name, allEntities, fkRelations);
                spc.AddSource($"{classSymbol.Name}_CryptoConfig.g.cs", configSource);

                // CryptoTableRegistry: every shadow table (domain + system). This is the
                // full map (EntityName, _crypto_<tableName>, openTableName) used by both
                // runtime layers that need to enumerate shadows (ex: benchmark, recovery,
                // BulkRotateKey scope filters).
                var registrySource = GenerateCryptoTableRegistry(ns, allEntities);
                spc.AddSource("CryptoTableRegistry.g.cs", registrySource);

                // SystemTableRegistry: filter to just the [SystemTable] entries. Used by
                // the worker's staged-apply routing (system tables first on import) and by
                // OwnershipTransferService to refuse transfer of system scopes.
                var systemRegistrySource = GenerateSystemTableRegistry(ns, systemEntities);
                spc.AddSource("SystemTableRegistry.g.cs", systemRegistrySource);

                // SensitiveEntityRegistry: filter to [Sensitive] entries (currently empty —
                // the attribute exists but no entity uses it yet).
                var sensitiveRegistrySource = GenerateSensitiveEntityRegistry(ns, sensitiveEntities);
                spc.AddSource("SensitiveEntityRegistry.g.cs", sensitiveRegistrySource);

                // Permission seed rows come ONLY from domain entities with [Permissions]
                // attributes. System tables have their own hardcoded permission seed in
                // CryptoSyncContextBase.SeedSystemTablePermissions — emitting duplicates here
                // would collide on the deterministic-GUID primary keys.
                var seedSource = GeneratePermissionSeedData(ns, classSymbol.Name, domainEntities);
                if (seedSource is not null)
                {
                    spc.AddSource($"{classSymbol.Name}_PermissionSeed.g.cs", seedSource);
                }
            }
        });
    }

    private static bool IsDbContextCandidate(SyntaxNode node)
    {
        return node is ClassDeclarationSyntax classDecl
            && classDecl.BaseList is not null
            && !classDecl.Modifiers.Any(SyntaxKind.AbstractKeyword);
    }

    private static bool InheritsFromCryptoSyncContextBase(INamedTypeSymbol classSymbol)
    {
        var current = classSymbol.BaseType;
        while (current is not null)
        {
            if (current.Name == "CryptoSyncContextBase")
            {
                return true;
            }
            current = current.BaseType;
        }
        return false;
    }

    private static bool InheritsSyncableEntity(INamedTypeSymbol entityType)
    {
        var current = entityType.BaseType;
        while (current is not null)
        {
            if (current.Name == "SyncableEntity")
            {
                return true;
            }
            current = current.BaseType;
        }
        return false;
    }

    private static bool HasAttribute(INamedTypeSymbol type, string attributeName)
    {
        foreach (var attr in type.GetAttributes())
        {
            if (attr.AttributeClass?.Name == attributeName)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Normalize a C# type symbol to the short name expected by the TypeScript
    /// <c>convertValueForSqlite</c> switch: <c>Guid</c>, <c>DateTime</c>, <c>Boolean</c>,
    /// <c>Enum</c>, <c>ByteArray</c>, etc. Uses the semantic model — no string heuristics.
    /// </summary>
    private static string NormalizeCSharpType(ITypeSymbol type)
    {
        // Nullable value type (Guid?, DateTime?, etc.)
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullableVt)
        {
            return NormalizeCSharpType(nullableVt.TypeArguments[0]) + "?";
        }

        // byte[] → "ByteArray"
        if (type is IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_Byte })
        {
            return "ByteArray";
        }

        // Enum → "Enum" (SharingScope, SyncRole, etc.)
        if (type.TypeKind == TypeKind.Enum)
        {
            return "Enum";
        }

        // Nullable reference type annotation (string?, etc.)
        var isNullableRef = type.NullableAnnotation == NullableAnnotation.Annotated;

        // ITypeSymbol.Name gives CLR name without namespace:
        // string→String, int→Int32, bool→Boolean, Guid→Guid, DateTime→DateTime
        var name = type.Name;

        return isNullableRef ? name + "?" : name;
    }

    /// <summary>
    /// All syncable entities reachable from the context — walks the concrete context AND the
    /// base context inheritance chain. Includes both domain entities (`IsSystemTable == false`)
    /// and system entities marked <c>[SystemTable]</c> (`IsSystemTable == true`). Both kinds
    /// need crypto shadow tables because both kinds are sync sources — system tables sync
    /// admin-managed state (contacts, permissions, sharing keys) to peers.
    ///
    /// Previously the walker stopped at <c>CryptoSyncContextBase</c> and skipped anything
    /// marked <c>[SystemTable]</c>. That meant the system DbSets declared on the base context
    /// (<c>TrustedContact</c>, <c>SyncPermission</c>, <c>SharingKey</c>) never got
    /// <c>Crypto_&lt;Entity&gt;</c> shadow classes or EF configuration, so there was no
    /// <c>_crypto_</c> table to sync against. Unified now: walk everything, tag system-ness,
    /// emit shadows for all.
    /// </summary>
    private static List<EntityInfo> GetSyncableEntities(INamedTypeSymbol contextSymbol)
    {
        var entities = new List<EntityInfo>();
        var seen = new HashSet<string>();
        var current = contextSymbol;

        while (current is not null)
        {
            foreach (var member in current.GetMembers())
            {
                if (member is not IPropertySymbol property)
                {
                    continue;
                }

                if (property.Type is not INamedTypeSymbol propertyType)
                {
                    continue;
                }

                if (propertyType.Name != "DbSet" || propertyType.TypeArguments.Length != 1)
                {
                    continue;
                }

                var entityType = (INamedTypeSymbol)propertyType.TypeArguments[0];

                // Include entities that either inherit SyncableEntity (domain entities with
                // Id / SharingScope / SharingId / UpdatedAt / IsDeleted) OR carry
                // [SystemTable] (system tables like SyncPermission, ColumnRegistryEntry).
                // Standalone classes like DeviceSettings, SentInvitation, ReceivedInvitation
                // have neither and are deliberately skipped — they're local-only.
                if (!InheritsSyncableEntity(entityType) && !HasAttribute(entityType, "SystemTableAttribute"))
                {
                    continue;
                }

                // Deduplicate across the inheritance chain. Concrete context might hide a base
                // DbSet with a new declaration; first wins.
                if (!seen.Add(entityType.Name))
                {
                    continue;
                }

                var isSystemTable = HasAttribute(entityType, "SystemTableAttribute");
                var properties = GetEntityProperties(entityType);
                var columnRules = GetColumnRules(entityType);
                var isSensitive = HasAttribute(entityType, "SensitiveAttribute");

                // System tables have their permission rules admin-seeded by
                // CryptoSyncContextBase.SeedSystemTablePermissions — the generator must NOT
                // emit duplicate seed rows for them. Domain entities walk [Permissions]
                // attributes as usual.
                var permissions = isSystemTable
                    ? new List<PermissionInfo>()
                    : GetPermissionAttributes(entityType, property.Name);

                entities.Add(new EntityInfo(
                    entityType.Name,
                    entityType.ContainingNamespace.ToDisplayString(),
                    property.Name,
                    properties,
                    permissions,
                    columnRules,
                    IsSystemTable: isSystemTable,
                    IsSensitive: isSensitive));
            }

            current = current.BaseType;
        }

        return entities;
    }

    private static List<PropertyInfo> GetEntityProperties(INamedTypeSymbol entityType)
    {
        var properties = new List<PropertyInfo>();
        var current = entityType;

        while (current is not null)
        {
            foreach (var member in current.GetMembers())
            {
                if (member is not IPropertySymbol prop)
                {
                    continue;
                }

                if (prop.DeclaredAccessibility != Accessibility.Public)
                {
                    continue;
                }

                if (prop.GetMethod is null || prop.SetMethod is null)
                {
                    continue;
                }

                // Skip navigation properties — both collections and single-entity references.
                // After stripping nullable, any class type except System.String is a navigation.
                var propType = prop.Type;
                if (propType is INamedTypeSymbol { IsGenericType: true, Name: "Nullable" } nullable)
                {
                    propType = nullable.TypeArguments[0];
                }

                // Strip nullable reference type annotation (e.g. ShareGroup?)
                propType = propType.WithNullableAnnotation(NullableAnnotation.None);

                if (propType is INamedTypeSymbol nt &&
                    nt.TypeKind == TypeKind.Class &&
                    nt.SpecialType != SpecialType.System_String)
                {
                    // Allow byte[] (IArrayTypeSymbol won't reach here, but guard against edge cases)
                    continue;
                }

                if (propType is IArrayTypeSymbol)
                {
                    // byte[] is fine — it's a BLOB column, not a navigation
                }
                else if (propType is INamedTypeSymbol collType &&
                    collType.IsGenericType &&
                    (collType.Name == "List" || collType.Name == "ICollection" || collType.Name == "IList"))
                {
                    continue;
                }

                properties.Add(new PropertyInfo(prop.Name, NormalizeCSharpType(prop.Type)));
            }

            current = current.BaseType;
        }

        return properties;
    }

    /// <summary>
    /// Walk the entity's properties for <c>[AllowUpdate]</c> / <c>[DenyUpdate]</c> markers
    /// and produce per-column role rules.
    /// </summary>
    private static List<ColumnRule> GetColumnRules(INamedTypeSymbol entityType)
    {
        var rules = new List<ColumnRule>();
        var current = entityType;

        while (current is not null)
        {
            foreach (var member in current.GetMembers())
            {
                if (member is not IPropertySymbol prop)
                {
                    continue;
                }

                foreach (var attr in prop.GetAttributes())
                {
                    var name = attr.AttributeClass?.Name;
                    if (name is not ("AllowUpdateAttribute" or "DenyUpdateAttribute"))
                    {
                        continue;
                    }

                    if (attr.ConstructorArguments.Length < 1)
                    {
                        continue;
                    }

                    var roles = attr.ConstructorArguments[0].Value?.ToString() ?? string.Empty;
                    var allow = name == "AllowUpdateAttribute";
                    rules.Add(new ColumnRule(prop.Name, roles, allow));
                }
            }

            current = current.BaseType;
        }

        return rules;
    }

    // ============================================================
    // CODE GENERATION — SHADOW + EF + REGISTRIES
    // ============================================================

    private static string GenerateCryptoEntity(string ns, EntityInfo entity)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("using System;");
        sb.AppendLine("using System.ComponentModel.DataAnnotations;");
        sb.AppendLine();
        sb.AppendLine($"namespace {ns};");
        sb.AppendLine();
        sb.AppendLine($"/// <summary>");
        sb.AppendLine($"/// Shadow crypto table for {entity.Name}. Contains encrypted row data.");
        sb.AppendLine($"/// Generated by CryptoSyncGenerator — do not edit.");
        sb.AppendLine($"/// </summary>");
        sb.AppendLine($"public sealed class Crypto_{entity.Name}");
        sb.AppendLine("{");
        sb.AppendLine("    public Guid Id { get; set; }");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>0=Public, 1=Shared, 2=Client</summary>");
        sb.AppendLine("    public int SharingScope { get; set; }");
        sb.AppendLine();
        sb.AppendLine("    [MaxLength(128)]");
        sb.AppendLine("    public required string SharingId { get; init; }");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>AES-GCM encrypted row data (ALL columns).</summary>");
        sb.AppendLine("    public required byte[] EncryptedRow { get; init; }");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>Per-row AES-GCM nonce.</summary>");
        sb.AppendLine("    public required byte[] Nonce { get; init; }");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>CEK version that encrypted this row (Layer 1 AAD).</summary>");
        sb.AppendLine("    public int KeyVersion { get; set; }");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>Ed25519 public key of the row producer (Layer 2 verification).</summary>");
        sb.AppendLine("    [MaxLength(64)]");
        sb.AppendLine("    public string SenderPublicKey { get; set; } = \"\";");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>Ed25519 per-row envelope signature (Layer 2 tamper detection).</summary>");
        sb.AppendLine("    public byte[] EnvelopeSignature { get; set; } = Array.Empty<byte>();");
        sb.AppendLine("}");

        return sb.ToString();
    }

    private static string GenerateEfConfiguration(string ns, string contextName, List<EntityInfo> entities, List<FkRelation> fkRelations)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("using Microsoft.EntityFrameworkCore;");
        sb.AppendLine();
        sb.AppendLine($"namespace {ns};");
        sb.AppendLine();
        sb.AppendLine($"public partial class {contextName}");
        sb.AppendLine("{");
        sb.AppendLine("    /// <summary>");
        sb.AppendLine("    /// Configure crypto shadow tables. Call from OnModelCreating.");
        sb.AppendLine("    /// Generated by CryptoSyncGenerator.");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine("    protected void ConfigureCryptoTables(ModelBuilder modelBuilder)");
        sb.AppendLine("    {");

        foreach (var entity in entities)
        {
            sb.AppendLine($"        modelBuilder.Entity<Crypto_{entity.Name}>(e =>");
            sb.AppendLine("        {");
            sb.AppendLine($"            e.ToTable(\"_crypto_{entity.DbSetName}\");");
            sb.AppendLine("            e.HasKey(x => x.Id);");
            sb.AppendLine("            e.HasIndex(x => x.SharingId);");
            sb.AppendLine("            e.HasIndex(x => x.SharingScope);");
            sb.AppendLine("        });");
            sb.AppendLine();
        }

        // Seed _column_registry with column metadata for each syncable entity.
        // The worker queries this at import time to build INSERT statements
        // with correct column order and type conversion.
        sb.AppendLine("        // Column registry: seeded schema metadata for worker import");
        sb.AppendLine("        modelBuilder.Entity<SqliteWasmBlazor.CryptoSync.ColumnRegistryEntry>().HasData(");

        var seedLines = new List<string>();
        foreach (var entity in entities)
        {
            for (var i = 0; i < entity.Properties.Count; i++)
            {
                var prop = entity.Properties[i];
                var sqlType = MapCSharpTypeToSqlType(prop.Type);
                var isPk = prop.Name == "Id";

                // Deterministic GUID from table + column index
                byte[] hash;
                using (var sha = System.Security.Cryptography.SHA256.Create())
                {
                    hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes($"ColumnRegistry:{entity.DbSetName}:{i}"));
                }
                var guidBytes = new byte[16];
                System.Array.Copy(hash, guidBytes, 16);
                var guid = new System.Guid(guidBytes);

                seedLines.Add(
                    $"            new {{ Id = System.Guid.Parse(\"{guid}\"), " +
                    $"TableName = \"{entity.DbSetName}\", " +
                    $"ColumnIndex = {i}, " +
                    $"ColumnName = \"{prop.Name}\", " +
                    $"SqlType = \"{sqlType}\", " +
                    $"CSharpType = \"{prop.Type}\", " +
                    $"IsPrimaryKey = {(isPk ? "true" : "false")} }}");
            }
        }

        sb.AppendLine(string.Join(",\n", seedLines));
        sb.AppendLine("        );");
        sb.AppendLine();

        sb.AppendLine("    }");
        sb.AppendLine();

        // Override GetChildFkRelations — delegates to SyncableFkMap for AOT-safe FK walks.
        sb.AppendLine("    public override (string ChildTable, string FkColumn)[] GetChildFkRelations(string parentTable)");
        sb.AppendLine("    {");
        sb.AppendLine("        var fks = SyncableFkMap.GetChildRelations(parentTable);");
        sb.AppendLine("        var result = new (string ChildTable, string FkColumn)[fks.Length];");
        sb.AppendLine("        for (var i = 0; i < fks.Length; i++)");
        sb.AppendLine("        {");
        sb.AppendLine("            result[i] = (fks[i].ChildTable, fks[i].FkColumn);");
        sb.AppendLine("        }");
        sb.AppendLine("        return result;");
        sb.AppendLine("    }");
        sb.AppendLine();

        // CloneForTransfer — per-entity switch that copies domain props, remaps FKs.
        var syncMetadataProps = new HashSet<string>
            { "Id", "SharingScope", "SharingId", "UpdatedAt", "IsDeleted", "DeletedAt" };

        // Build FK column set per entity (DbSetName → set of FK column names).
        var fkColumnsByEntity = new Dictionary<string, HashSet<string>>();
        foreach (var rel in fkRelations)
        {
            if (!fkColumnsByEntity.TryGetValue(rel.ChildTable, out var cols))
            {
                cols = new HashSet<string>();
                fkColumnsByEntity[rel.ChildTable] = cols;
            }
            cols.Add(rel.FkColumn);
        }

        sb.AppendLine("    public override SqliteWasmBlazor.CryptoSync.SyncableEntity CloneForTransfer(");
        sb.AppendLine("        SqliteWasmBlazor.CryptoSync.SyncableEntity source,");
        sb.AppendLine("        System.Collections.Generic.Dictionary<System.Guid, System.Guid> idMap)");
        sb.AppendLine("    {");
        sb.AppendLine("        return source switch");
        sb.AppendLine("        {");

        foreach (var entity in entities)
        {
            fkColumnsByEntity.TryGetValue(entity.DbSetName, out var entityFks);
            var fkSet = entityFks ?? new HashSet<string>();

            var assignments = new List<string>();
            foreach (var prop in entity.Properties)
            {
                if (syncMetadataProps.Contains(prop.Name))
                {
                    continue;
                }

                if (fkSet.Contains(prop.Name))
                {
                    // FK column — remap via idMap. Use TryGetValue fallback for
                    // FKs that point outside the transferred subtree.
                    assignments.Add($"{prop.Name} = idMap.TryGetValue(e.{prop.Name}, out var mapped_{prop.Name}) ? mapped_{prop.Name} : e.{prop.Name}");
                }
                else
                {
                    assignments.Add($"{prop.Name} = e.{prop.Name}");
                }
            }

            var body = string.Join(", ", assignments);
            sb.AppendLine($"            {entity.Namespace}.{entity.Name} e => new {entity.Namespace}.{entity.Name} {{ {body} }},");
        }

        sb.AppendLine("            _ => throw new System.InvalidOperationException($\"CloneForTransfer: unknown entity type {source.GetType().Name}\")");
        sb.AppendLine("        };");
        sb.AppendLine("    }");

        sb.AppendLine("}");

        return sb.ToString();
    }

    /// <summary>
    /// Map a normalized C# type name (from <see cref="NormalizeCSharpType"/>) to the
    /// SQLite column type that EF Core uses. Input is already short/CLR-named.
    /// </summary>
    private static string MapCSharpTypeToSqlType(string csharpType)
    {
        // Strip nullable suffix
        var baseType = csharpType.EndsWith("?") ? csharpType.Substring(0, csharpType.Length - 1) : csharpType;

        return baseType switch
        {
            "Guid" or "String" or "DateTime" or "DateTimeOffset" or "TimeSpan"
                or "Decimal" or "Char" => "TEXT",
            "Boolean" or "Int32" or "Int64" or "Int16" or "Byte"
                or "UInt32" or "UInt64" or "UInt16" or "SByte"
                or "Enum" => "INTEGER",
            "Double" or "Single" => "REAL",
            "ByteArray" => "BLOB",
            _ => "TEXT"
        };
    }

    private static string GenerateCryptoTableRegistry(string ns, List<EntityInfo> entities)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine($"namespace {ns};");
        sb.AppendLine();
        sb.AppendLine("/// <summary>");
        sb.AppendLine("/// Registry of all crypto shadow tables. Generated by CryptoSyncGenerator.");
        sb.AppendLine("/// </summary>");
        sb.AppendLine("public static class CryptoTableRegistry");
        sb.AppendLine("{");
        sb.AppendLine("    public static readonly (string EntityName, string CryptoTableName, string OpenTableName)[] Tables =");
        sb.AppendLine("    [");

        foreach (var entity in entities)
        {
            sb.AppendLine($"        (\"{entity.Name}\", \"_crypto_{entity.DbSetName}\", \"{entity.DbSetName}\"),");
        }

        sb.AppendLine("    ];");
        sb.AppendLine("}");

        return sb.ToString();
    }

    private static string GenerateSystemTableRegistry(string ns, List<EntityInfo> entities)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine($"namespace {ns};");
        sb.AppendLine();
        sb.AppendLine("/// <summary>");
        sb.AppendLine("/// Registry of all system tables (entities marked <c>[SystemTable]</c>).");
        sb.AppendLine("/// Source of truth for ownership-transfer refusal — system scopes are locked");
        sb.AppendLine("/// to the admin device and cannot be transferred.");
        sb.AppendLine("/// Generated by CryptoSyncGenerator.");
        sb.AppendLine("/// </summary>");
        sb.AppendLine("public static class SystemTableRegistry");
        sb.AppendLine("{");
        sb.AppendLine("    public static readonly (string EntityName, string TableName)[] Tables =");
        sb.AppendLine("    [");

        foreach (var entity in entities)
        {
            sb.AppendLine($"        (\"{entity.Name}\", \"{entity.DbSetName}\"),");
        }

        sb.AppendLine("    ];");
        sb.AppendLine();
        sb.AppendLine("    public static bool IsSystem(string tableName)");
        sb.AppendLine("    {");
        sb.AppendLine("        foreach (var (_, t) in Tables)");
        sb.AppendLine("        {");
        sb.AppendLine("            if (t == tableName) return true;");
        sb.AppendLine("        }");
        sb.AppendLine("        return false;");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        return sb.ToString();
    }

    private static string GenerateSensitiveEntityRegistry(string ns, List<EntityInfo> entities)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine($"namespace {ns};");
        sb.AppendLine();
        sb.AppendLine("/// <summary>");
        sb.AppendLine("/// Registry of all sensitive entities (marked <c>[Sensitive]</c>).");
        sb.AppendLine("/// Sensitive entities live only in the encrypted shadow — they have no");
        sb.AppendLine("/// plaintext open-table copy and are accessed only via SensitiveAccessService.");
        sb.AppendLine("/// Generated by CryptoSyncGenerator.");
        sb.AppendLine("/// </summary>");
        sb.AppendLine("public static class SensitiveEntityRegistry");
        sb.AppendLine("{");
        sb.AppendLine("    public static readonly (string EntityName, string TableName)[] Tables =");
        sb.AppendLine("    [");

        foreach (var entity in entities)
        {
            sb.AppendLine($"        (\"{entity.Name}\", \"{entity.DbSetName}\"),");
        }

        sb.AppendLine("    ];");
        sb.AppendLine();
        sb.AppendLine("    public static bool IsSensitive(string tableName)");
        sb.AppendLine("    {");
        sb.AppendLine("        foreach (var (_, t) in Tables)");
        sb.AppendLine("        {");
        sb.AppendLine("            if (t == tableName) return true;");
        sb.AppendLine("        }");
        sb.AppendLine("        return false;");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        return sb.ToString();
    }

    // ============================================================
    // PERMISSION SEED DATA GENERATION
    // ============================================================

    /// <summary>
    /// Walk the entity for <c>[Permissions]</c> attributes. Each attribute carries a
    /// default role plus optional <c>Create/Read/Update/Delete</c> overrides — all
    /// strings parsed against <c>SyncRole</c> (or <c>"Any"</c> for the wildcard).
    /// Multiple attributes stack — they're merged when emitting seed rows.
    /// </summary>
    private static List<PermissionInfo> GetPermissionAttributes(INamedTypeSymbol entityType, string dbSetName)
    {
        var permissions = new List<PermissionInfo>();

        foreach (var attr in entityType.GetAttributes())
        {
            if (attr.AttributeClass?.Name != "PermissionsAttribute")
            {
                continue;
            }

            // defaultRole is the constructor's optional first arg (string, default "Any").
            var defaultRole = attr.ConstructorArguments.Length >= 1
                ? attr.ConstructorArguments[0].Value?.ToString() ?? AnyRole
                : AnyRole;

            string create = defaultRole, read = defaultRole, update = defaultRole, delete = defaultRole;

            foreach (var named in attr.NamedArguments)
            {
                var value = named.Value.Value?.ToString() ?? defaultRole;
                switch (named.Key)
                {
                    case "Create": create = value; break;
                    case "Read":   read = value; break;
                    case "Update": update = value; break;
                    case "Delete": delete = value; break;
                }
            }

            permissions.Add(new PermissionInfo(dbSetName, create, read, update, delete));
        }

        return permissions;
    }

    /// <summary>
    /// Returns true iff <paramref name="role"/> meets the minimum-role requirement
    /// expressed by <paramref name="requiredRole"/>. <c>"Any"</c> means no requirement
    /// (all roles pass). Concrete roles are compared by enum index where Owner=0,
    /// Editor=1, Viewer=2 — lower index = higher privilege, so a role passes if its
    /// index is &lt;= the required role's index.
    /// </summary>
    private static bool RolePasses(int role, string requiredRole)
    {
        if (requiredRole == AnyRole)
        {
            return true;
        }

        var requiredIndex = ParseRole(requiredRole);
        if (requiredIndex is null)
        {
            return true; // Unparseable — fail open rather than emit a broken seed.
        }

        return role <= requiredIndex.Value;
    }

    private static int? ParseRole(string roleName) => roleName switch
    {
        "Owner" => 0,
        "Editor" => 1,
        "Viewer" => 2,
        _ => null
    };

    private static string? GeneratePermissionSeedData(string ns, string contextName, List<EntityInfo> entities)
    {
        var seedRows = new List<ResolvedPermission>();

        foreach (var entity in entities)
        {
            if (entity.Permissions.Count == 0 && entity.ColumnRules.Count == 0)
            {
                continue;
            }

            foreach (var role in new[] { 0, 1, 2 }) // Owner, Editor, Viewer
            {
                seedRows.Add(BuildResolvedPermission(entity, role));
            }
        }

        if (seedRows.Count == 0)
        {
            return null;
        }

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("using Microsoft.EntityFrameworkCore;");
        sb.AppendLine("using SqliteWasmBlazor.CryptoSync;");
        sb.AppendLine();
        sb.AppendLine($"namespace {ns};");
        sb.AppendLine();
        sb.AppendLine($"public partial class {contextName}");
        sb.AppendLine("{");
        sb.AppendLine("    /// <summary>");
        sb.AppendLine("    /// Seed fully resolved permission data from [Permissions] attributes.");
        sb.AppendLine("    /// Call from OnModelCreating. Generated by CryptoSyncGenerator.");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine("    protected void SeedPermissions(ModelBuilder modelBuilder)");
        sb.AppendLine("    {");
        sb.AppendLine("        modelBuilder.Entity<SyncPermission>().HasData(");

        for (var i = 0; i < seedRows.Count; i++)
        {
            var p = seedRows[i];
            // Deterministic GUID: SHA-256 of "DomainPermission:{role}:{table}" truncated to 16 bytes.
            byte[] hash;
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                hash = sha.ComputeHash(Encoding.UTF8.GetBytes($"DomainPermission:{p.Role}:{p.TableName}"));
            }
            var guidBytes = new byte[16];
            Array.Copy(hash, guidBytes, 16);
            var guid = new Guid(guidBytes);

            sb.Append($"            new SyncPermission {{ Id = System.Guid.Parse(\"{guid}\"), ");
            sb.Append($"Role = (SyncRole){p.Role}, ");
            sb.Append($"TableName = \"{p.TableName}\", ");
            sb.Append($"CanInsert = {(p.CanInsert ? "true" : "false")}, ");
            sb.Append($"CanRead = {(p.CanRead ? "true" : "false")}, ");
            sb.Append($"CanUpdate = {(p.CanUpdate ? "true" : "false")}, ");
            sb.Append($"CanDelete = {(p.CanDelete ? "true" : "false")}, ");
            sb.Append($"ReadonlyColumns = \"{p.ReadonlyColumns}\", ");
            sb.Append($"ReadwriteColumns = \"{p.ReadwriteColumns}\", ");
            sb.Append($"SharingScope = SharingScope.Public, SharingId = \"system\", ");
            sb.Append($"UpdatedAt = new System.DateTime(2026, 1, 1, 0, 0, 0, System.DateTimeKind.Utc)");
            sb.Append(" }");
            sb.AppendLine(i < seedRows.Count - 1 ? "," : "");
        }

        sb.AppendLine("        );");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        return sb.ToString();
    }

    /// <summary>
    /// Fully resolve permissions for one (entity, role) pair. Merges stacked
    /// <c>[Permissions]</c> attributes and resolves <c>[AllowUpdate]</c> /
    /// <c>[DenyUpdate]</c> column overrides into flat boolean fields.
    /// </summary>
    private static ResolvedPermission BuildResolvedPermission(EntityInfo entity, int role)
    {
        // Merge stacked [Permissions] attributes — most-restrictive wins per CRUD slot.
        var create = AnyRole; var read = AnyRole; var update = AnyRole; var delete = AnyRole;

        foreach (var perm in entity.Permissions)
        {
            create = TighterRole(create, perm.Create);
            read = TighterRole(read, perm.Read);
            update = TighterRole(update, perm.Update);
            delete = TighterRole(delete, perm.Delete);
        }

        var canInsert = RolePasses(role, create);
        var canRead = RolePasses(role, read);
        var canUpdate = RolePasses(role, update);
        var canDelete = RolePasses(role, delete);

        // Column overrides relative to the table-level Update rule.
        var readonlyColumns = new List<string>();
        var readwriteColumns = new List<string>();

        foreach (var rule in entity.ColumnRules)
        {
            if (!ContainsRole(rule.Roles, role))
            {
                continue;
            }

            if (rule.Allow && !canUpdate)
            {
                // Table-level update denied, but this column is exempted.
                readwriteColumns.Add(rule.ColumnName);
            }
            else if (!rule.Allow && canUpdate)
            {
                // Table-level update allowed, but this column is restricted.
                readonlyColumns.Add(rule.ColumnName);
            }
        }

        return new ResolvedPermission(
            role, entity.DbSetName,
            canInsert, canRead, canUpdate, canDelete,
            string.Join(",", readonlyColumns),
            string.Join(",", readwriteColumns));
    }

    /// <summary>
    /// Returns the more restrictive (lower-index) of two role strings.
    /// "Any" is the loosest; "Owner" is the strictest.
    /// </summary>
    private static string TighterRole(string a, string b)
    {
        if (a == AnyRole) return b;
        if (b == AnyRole) return a;
        var ai = ParseRole(a) ?? int.MaxValue;
        var bi = ParseRole(b) ?? int.MaxValue;
        return ai <= bi ? a : b;
    }

    /// <summary>
    /// True if a comma-separated role list (as written in <c>[AllowUpdate("Editor,Viewer")]</c>)
    /// contains the given role.
    /// </summary>
    private static bool ContainsRole(string roleList, int role)
    {
        var target = role switch { 0 => "Owner", 1 => "Editor", 2 => "Viewer", _ => "" };
        foreach (var name in roleList.Split(','))
        {
            if (name.Trim() == target)
            {
                return true;
            }
        }
        return false;
    }

    // ============================================================
    // CODE GENERATION — FK RELATION MAP
    // ============================================================

    /// <summary>
    /// Discover FK relationships between syncable entities by walking navigation
    /// properties. For each entity, finds single-entity navigation properties
    /// whose type is another syncable entity, then matches the FK column via
    /// convention (<c>{NavigationName}Id</c>) or <c>[ForeignKey]</c> attribute.
    /// </summary>
    private static List<FkRelation> DiscoverFkRelations(
        List<EntityInfo> allEntities,
        INamedTypeSymbol contextSymbol)
    {
        var entityNameToDbSet = new Dictionary<string, string>();
        foreach (var entity in allEntities)
        {
            entityNameToDbSet[entity.Name] = entity.DbSetName;
        }

        var relations = new List<FkRelation>();

        // Walk every DbSet property again to get the INamedTypeSymbol for each entity.
        var current = contextSymbol;
        while (current is not null)
        {
            foreach (var member in current.GetMembers())
            {
                if (member is not IPropertySymbol property)
                {
                    continue;
                }
                if (property.Type is not INamedTypeSymbol propertyType)
                {
                    continue;
                }
                if (propertyType.Name != "DbSet" || propertyType.TypeArguments.Length != 1)
                {
                    continue;
                }

                var entityType = (INamedTypeSymbol)propertyType.TypeArguments[0];
                if (!entityNameToDbSet.ContainsKey(entityType.Name))
                {
                    continue;
                }

                var childDbSet = entityNameToDbSet[entityType.Name];
                DiscoverNavigationFks(entityType, childDbSet, entityNameToDbSet, relations);
            }
            current = current.BaseType;
        }

        return relations;
    }

    private static void DiscoverNavigationFks(
        INamedTypeSymbol entityType,
        string childDbSet,
        Dictionary<string, string> entityNameToDbSet,
        List<FkRelation> relations)
    {
        var entityMembers = entityType;
        while (entityMembers is not null)
        {
            foreach (var member in entityMembers.GetMembers())
            {
                if (member is not IPropertySymbol prop)
                {
                    continue;
                }
                if (prop.DeclaredAccessibility != Accessibility.Public)
                {
                    continue;
                }

                // Strip nullable annotation to get the raw type.
                var navType = prop.Type.WithNullableAnnotation(NullableAnnotation.None);
                if (navType is INamedTypeSymbol { IsGenericType: true, Name: "Nullable" } nullable)
                {
                    navType = nullable.TypeArguments[0];
                }

                // Only single-entity navigations (class type, not string, not collection).
                if (navType is not INamedTypeSymbol nt)
                {
                    continue;
                }
                if (nt.TypeKind != TypeKind.Class || nt.SpecialType == SpecialType.System_String)
                {
                    continue;
                }
                if (nt.IsGenericType)
                {
                    continue;
                }

                // Navigation must point to a known syncable entity.
                if (!entityNameToDbSet.TryGetValue(nt.Name, out var parentDbSet))
                {
                    continue;
                }

                // Resolve FK column: check [ForeignKey] attribute on the nav property first,
                // then fall back to convention {NavigationName}Id.
                var fkColumn = GetForeignKeyFromAttribute(prop)
                    ?? $"{prop.Name}Id";

                // Verify the FK column actually exists as a scalar property on this entity.
                if (!HasScalarProperty(entityType, fkColumn))
                {
                    continue;
                }

                relations.Add(new FkRelation(childDbSet, fkColumn, parentDbSet));
            }
            entityMembers = entityMembers.BaseType;
        }
    }

    private static string? GetForeignKeyFromAttribute(IPropertySymbol property)
    {
        foreach (var attr in property.GetAttributes())
        {
            if (attr.AttributeClass?.Name == "ForeignKeyAttribute" && attr.ConstructorArguments.Length >= 1)
            {
                return attr.ConstructorArguments[0].Value?.ToString();
            }
        }
        return null;
    }

    private static bool HasScalarProperty(INamedTypeSymbol entityType, string propertyName)
    {
        var current = entityType;
        while (current is not null)
        {
            foreach (var member in current.GetMembers())
            {
                if (member is IPropertySymbol prop && prop.Name == propertyName)
                {
                    return true;
                }
            }
            current = current.BaseType;
        }
        return false;
    }

    private static string GenerateSyncableFkMap(string ns, List<FkRelation> relations)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine($"namespace {ns};");
        sb.AppendLine();
        sb.AppendLine("/// <summary>");
        sb.AppendLine("/// Static FK relation map for syncable entities. Generated by CryptoSyncGenerator.");
        sb.AppendLine("/// Used by SyncableFkCascade for AOT-safe subtree walks without runtime EF metadata.");
        sb.AppendLine("/// </summary>");
        sb.AppendLine("public static class SyncableFkMap");
        sb.AppendLine("{");
        sb.AppendLine("    public readonly record struct FkRelation(string ChildTable, string FkColumn, string ParentTable);");
        sb.AppendLine();
        sb.AppendLine("    public static FkRelation[] GetChildRelations(string parentTable)");
        sb.AppendLine("    {");
        sb.AppendLine("        return parentTable switch");
        sb.AppendLine("        {");

        // Group relations by parent table.
        var byParent = new Dictionary<string, List<FkRelation>>();
        foreach (var rel in relations)
        {
            if (!byParent.TryGetValue(rel.ParentTable, out var list))
            {
                list = new List<FkRelation>();
                byParent[rel.ParentTable] = list;
            }
            list.Add(rel);
        }

        foreach (var kv in byParent.OrderBy(x => x.Key))
        {
            var entries = string.Join(", ", kv.Value
                .OrderBy(c => c.ChildTable)
                .Select(c => $"new(\"{c.ChildTable}\", \"{c.FkColumn}\", \"{c.ParentTable}\")"));
            sb.AppendLine($"            \"{kv.Key}\" => [{entries}],");
        }

        sb.AppendLine("            _ => []");
        sb.AppendLine("        };");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        return sb.ToString();
    }

    // ============================================================
    // DATA MODELS
    // ============================================================

    private sealed record EntityInfo(
        string Name,
        string Namespace,
        string DbSetName,
        List<PropertyInfo> Properties,
        List<PermissionInfo> Permissions,
        List<ColumnRule> ColumnRules,
        bool IsSystemTable,
        bool IsSensitive);

    private sealed record PropertyInfo(string Name, string Type);

    private sealed record FkRelation(string ChildTable, string FkColumn, string ParentTable);

    /// <summary>
    /// One <c>[Permissions]</c> attribute lowered to its CRUD role slots.
    /// Each role slot is a string ("Any" / "Owner" / "Editor" / "Viewer").
    /// Multiple attributes on the same entity stack — Tighter wins per slot.
    /// </summary>
    private sealed record PermissionInfo(
        string TableName,
        string Create,
        string Read,
        string Update,
        string Delete);

    private sealed record ColumnRule(string ColumnName, string Roles, bool Allow);

    private sealed record ResolvedPermission(
        int Role,
        string TableName,
        bool CanInsert,
        bool CanRead,
        bool CanUpdate,
        bool CanDelete,
        string ReadonlyColumns,
        string ReadwriteColumns);
}
