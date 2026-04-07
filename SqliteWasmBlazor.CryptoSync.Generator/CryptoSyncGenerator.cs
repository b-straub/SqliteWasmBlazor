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
        "SentInvitation", "ReceivedInvitation", "DeviceSettings", "SyncState"
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

                // Concrete-context domain entities (used for shadow tables, EF config,
                // CryptoTableRegistry, permission seeds).
                var domainEntities = GetDomainEntities(classSymbol);

                // System-table entities (used for SystemTableRegistry). Walks base context too.
                var systemEntities = GetSystemTableEntities(classSymbol);

                // Sensitive entities (used for SensitiveEntityRegistry).
                var sensitiveEntities = domainEntities.Where(e => e.IsSensitive).ToList();

                if (domainEntities.Count == 0 && systemEntities.Count == 0)
                {
                    continue;
                }

                var ns = classSymbol.ContainingNamespace.ToDisplayString();

                // Generate _crypto_ entity for each domain entity (skip sensitive — they
                // live only in the shadow path; the entity stays declared by the user but
                // open-table query access is blocked at the SensitiveAccessService layer).
                foreach (var entity in domainEntities)
                {
                    var source = GenerateCryptoEntity(ns, entity);
                    spc.AddSource($"Crypto_{entity.Name}.g.cs", source);
                }

                // Generate OnModelCreating extension for all crypto tables
                var configSource = GenerateEfConfiguration(ns, classSymbol.Name, domainEntities);
                spc.AddSource($"{classSymbol.Name}_CryptoConfig.g.cs", configSource);

                // Generate crypto table registry (domain entities)
                var registrySource = GenerateCryptoTableRegistry(ns, domainEntities);
                spc.AddSource("CryptoTableRegistry.g.cs", registrySource);

                // Generate system table registry (collected from concrete + base context)
                var systemRegistrySource = GenerateSystemTableRegistry(ns, systemEntities);
                spc.AddSource("SystemTableRegistry.g.cs", systemRegistrySource);

                // Generate sensitive entity registry
                var sensitiveRegistrySource = GenerateSensitiveEntityRegistry(ns, sensitiveEntities);
                spc.AddSource("SensitiveEntityRegistry.g.cs", sensitiveRegistrySource);

                // Generate permission seed data from [Permissions] attributes on domain entities
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
    /// Concrete-context domain entities — DbSet&lt;T&gt; declared on the user's context,
    /// not on the base. Excludes system tables (those flow into SystemTableRegistry instead).
    /// </summary>
    private static List<EntityInfo> GetDomainEntities(INamedTypeSymbol contextSymbol)
    {
        var entities = new List<EntityInfo>();
        var current = contextSymbol;

        while (current is not null)
        {
            // Only the concrete context's own DbSet declarations.
            if (current.Name == "CryptoSyncContextBase")
            {
                break;
            }

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

                // Domain entities must inherit SyncableEntity (so the row can sync).
                if (!InheritsSyncableEntity(entityType))
                {
                    continue;
                }

                // Skip [SystemTable] markers — they flow into SystemTableRegistry instead.
                if (HasAttribute(entityType, "SystemTableAttribute"))
                {
                    continue;
                }

                var properties = GetEntityProperties(entityType);
                var permissions = GetPermissionAttributes(entityType, property.Name);
                var columnRules = GetColumnRules(entityType);
                var isSensitive = HasAttribute(entityType, "SensitiveAttribute");

                entities.Add(new EntityInfo(
                    entityType.Name,
                    entityType.ContainingNamespace.ToDisplayString(),
                    property.Name,
                    properties,
                    permissions,
                    columnRules,
                    IsSystemTable: false,
                    IsSensitive: isSensitive));
            }

            current = current.BaseType;
        }

        return entities;
    }

    /// <summary>
    /// All entities marked <c>[SystemTable]</c>, walking the entire context inheritance
    /// chain (concrete + base). Used to emit <c>SystemTableRegistry</c>.
    /// </summary>
    private static List<EntityInfo> GetSystemTableEntities(INamedTypeSymbol contextSymbol)
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

                if (!HasAttribute(entityType, "SystemTableAttribute"))
                {
                    continue;
                }

                if (!seen.Add(entityType.Name))
                {
                    continue;
                }

                entities.Add(new EntityInfo(
                    entityType.Name,
                    entityType.ContainingNamespace.ToDisplayString(),
                    property.Name,
                    Properties: new List<PropertyInfo>(),
                    Permissions: new List<PermissionInfo>(),
                    ColumnRules: new List<ColumnRule>(),
                    IsSystemTable: true,
                    IsSensitive: false));
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

                // Skip navigation properties (collections)
                if (prop.Type is INamedTypeSymbol namedType &&
                    namedType.IsGenericType &&
                    (namedType.Name == "List" || namedType.Name == "ICollection" || namedType.Name == "IList"))
                {
                    continue;
                }

                properties.Add(new PropertyInfo(prop.Name, prop.Type.ToDisplayString()));
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
        sb.AppendLine("}");

        return sb.ToString();
    }

    private static string GenerateEfConfiguration(string ns, string contextName, List<EntityInfo> entities)
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

        sb.AppendLine("    }");
        sb.AppendLine("}");

        return sb.ToString();
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
        // Collect (Role, TableName) → diff JSON for every domain entity that has any
        // permission attribute or column rule. Owner ALWAYS gets `{}` (full access).
        var seedRows = new List<(int Role, string TableName, string DiffJson)>();

        foreach (var entity in entities)
        {
            if (entity.Permissions.Count == 0 && entity.ColumnRules.Count == 0)
            {
                continue;
            }

            foreach (var role in new[] { 0, 1, 2 }) // Owner, Editor, Viewer
            {
                var diff = BuildPermissionDiffJson(entity, role);
                seedRows.Add((role, entity.DbSetName, diff));
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
        sb.AppendLine("    /// Seed permission data from [Permissions] attributes on domain entities.");
        sb.AppendLine("    /// Call from OnModelCreating. Generated by CryptoSyncGenerator.");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine("    protected void SeedPermissions(ModelBuilder modelBuilder)");
        sb.AppendLine("    {");
        sb.AppendLine("        modelBuilder.Entity<SyncPermission>().HasData(");

        for (var i = 0; i < seedRows.Count; i++)
        {
            var (role, table, diffJson) = seedRows[i];
            // Deterministic GUID derived from SHA-256 of "DomainPermission:{role}:{table}"
            // truncated to 16 bytes. Matches CryptoSyncContextBase.DeterministicGuid —
            // SHA-256 (not MD5) because Blazor WASM's runtime crypto does not ship MD5.
            // Using the Create() form because the generator targets netstandard2.0
            // which pre-dates the static HashData helper.
            byte[] hash;
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                hash = sha.ComputeHash(Encoding.UTF8.GetBytes($"DomainPermission:{role}:{table}"));
            }
            var guidBytes = new byte[16];
            Array.Copy(hash, guidBytes, 16);
            var guid = new Guid(guidBytes);

            sb.Append($"            new SyncPermission {{ Id = System.Guid.Parse(\"{guid}\"), ");
            sb.Append($"Role = (SyncRole){role}, ");
            sb.Append($"TableName = \"{table}\", ");
            sb.Append($"PermissionDiffJson = @\"{diffJson.Replace("\"", "\"\"")}\", ");
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
    /// Build the nested-per-table permission diff for one (entity, role) pair.
    /// Format (PermissionDiffSchemaVersion = 2):
    /// <c>{ "TableName": { "delete": "deny", "insert": "deny", "columns": { "Price": "readonly" } } }</c>
    /// Owner-style full-access emits <c>{}</c>. Only denials and column overrides appear in the diff.
    /// </summary>
    private static string BuildPermissionDiffJson(EntityInfo entity, int role)
    {
        // Merge stacked [Permissions] attributes — most-restrictive wins per CRUD slot.
        // The convention is one attribute per entity; multi-stacking is rare and conservative.
        var create = AnyRole; var read = AnyRole; var update = AnyRole; var delete = AnyRole;

        foreach (var perm in entity.Permissions)
        {
            create = TighterRole(create, perm.Create);
            read = TighterRole(read, perm.Read);
            update = TighterRole(update, perm.Update);
            delete = TighterRole(delete, perm.Delete);
        }

        var insertDenied = !RolePasses(role, create);
        var readDenied = !RolePasses(role, read);
        var updateDenied = !RolePasses(role, update);
        var deleteDenied = !RolePasses(role, delete);

        var actionEntries = new List<string>();
        if (insertDenied) actionEntries.Add("\"\"insert\"\":\"\"deny\"\"");
        if (readDenied)   actionEntries.Add("\"\"read\"\":\"\"deny\"\"");
        if (updateDenied) actionEntries.Add("\"\"update\"\":\"\"deny\"\"");
        if (deleteDenied) actionEntries.Add("\"\"delete\"\":\"\"deny\"\"");

        // Column overrides relative to the table-level Update rule.
        // [AllowUpdate(roles)]: listed roles GAIN update access on this column.
        //   Only relevant when this role is denied update at the table level.
        // [DenyUpdate(roles)]:  listed roles LOSE update access on this column.
        //   Only relevant when this role is allowed update at the table level.
        var columnEntries = new List<string>();
        foreach (var rule in entity.ColumnRules)
        {
            var listed = ContainsRole(rule.Roles, role);
            if (!listed)
            {
                continue;
            }

            if (rule.Allow && updateDenied)
            {
                // Table-level update is denied for this role, but this column is exempted.
                columnEntries.Add($"\"\"{rule.ColumnName}\"\":\"\"readwrite\"\"");
            }
            else if (!rule.Allow && !updateDenied)
            {
                // Table-level update is allowed for this role, but this column is restricted.
                columnEntries.Add($"\"\"{rule.ColumnName}\"\":\"\"readonly\"\"");
            }
        }

        if (actionEntries.Count == 0 && columnEntries.Count == 0)
        {
            return "{}";
        }

        var inner = new StringBuilder();
        inner.Append("{");
        var parts = new List<string>(actionEntries);
        if (columnEntries.Count > 0)
        {
            parts.Add("\"\"columns\"\":{" + string.Join(",", columnEntries) + "}");
        }
        inner.Append(string.Join(",", parts));
        inner.Append("}");

        return $"{{\"\"{entity.DbSetName}\"\":{inner}}}";
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
}
