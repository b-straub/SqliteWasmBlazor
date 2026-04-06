using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SqliteWasmBlazor.CryptoSync.Generator;

[Generator]
public class CryptoSyncGenerator : IIncrementalGenerator
{
    // System tables from CryptoSyncContextBase — skip these for shadow generation
    private static readonly HashSet<string> SystemTableTypes = new()
    {
        "TrustedContact", "SentInvitation", "ReceivedInvitation",
        "SharingKey", "SyncPermission", "SyncState", "DeviceSettings"
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

                // Find all DbSet<T> properties — these are the domain entities
                var entities = GetDbSetEntities(classSymbol);
                if (entities.Count == 0)
                {
                    continue;
                }

                var ns = classSymbol.ContainingNamespace.ToDisplayString();

                // Generate _crypto_ entity for each domain entity
                foreach (var entity in entities)
                {
                    var source = GenerateCryptoEntity(ns, entity);
                    spc.AddSource($"Crypto_{entity.Name}.g.cs", source);
                }

                // Generate OnModelCreating extension for all crypto tables
                var configSource = GenerateEfConfiguration(ns, classSymbol.Name, entities);
                spc.AddSource($"{classSymbol.Name}_CryptoConfig.g.cs", configSource);

                // Generate crypto table registry
                var registrySource = GenerateRegistry(ns, entities);
                spc.AddSource("CryptoTableRegistry.g.cs", registrySource);

                // Generate permission seed data from [SyncPermission] attributes
                var seedSource = GeneratePermissionSeedData(ns, classSymbol.Name, entities);
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

    private static List<EntityInfo> GetDbSetEntities(INamedTypeSymbol contextSymbol)
    {
        var entities = new List<EntityInfo>();

        // Walk inheritance chain to find all DbSet<T> properties
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

                // Skip system tables from base context
                if (SystemTableTypes.Contains(entityType.Name))
                {
                    continue;
                }

                // Only include entities declared in the concrete context (not base)
                if (current.Name == "CryptoSyncContextBase")
                {
                    continue;
                }

                var properties = GetEntityProperties(entityType);
                var permissions = GetPermissionAttributes(entityType, property.Name);
                entities.Add(new EntityInfo(
                    entityType.Name,
                    entityType.ContainingNamespace.ToDisplayString(),
                    property.Name,
                    properties,
                    permissions));
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

    // ============================================================
    // CODE GENERATION
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

    private static string GenerateRegistry(string ns, List<EntityInfo> entities)
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

    // ============================================================
    // PERMISSION SEED DATA GENERATION
    // ============================================================

    private static List<PermissionInfo> GetPermissionAttributes(INamedTypeSymbol entityType, string dbSetName)
    {
        var permissions = new List<PermissionInfo>();

        foreach (var attr in entityType.GetAttributes())
        {
            if (attr.AttributeClass?.Name is not ("SyncPermissionAttribute" or "SyncPermission"))
            {
                continue;
            }

            if (attr.ConstructorArguments.Length < 1)
            {
                continue;
            }

            var role = (int)attr.ConstructorArguments[0].Value!;
            var access = attr.ConstructorArguments.Length > 1
                ? attr.ConstructorArguments[1].Value?.ToString() ?? "readwrite"
                : "readwrite";

            string[]? readWriteColumns = null;
            foreach (var named in attr.NamedArguments)
            {
                if (named.Key == "ReadWriteColumns" && !named.Value.IsNull)
                {
                    readWriteColumns = named.Value.Values.Select(v => v.Value?.ToString() ?? "").ToArray();
                }
            }

            permissions.Add(new PermissionInfo(role, access, dbSetName, readWriteColumns));
        }

        return permissions;
    }

    private static string? GeneratePermissionSeedData(string ns, string contextName, List<EntityInfo> entities)
    {
        var allPermissions = entities.SelectMany(e => e.Permissions).ToList();
        if (allPermissions.Count == 0)
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
        sb.AppendLine("    /// Seed permission data from [SyncPermission] attributes.");
        sb.AppendLine("    /// Call from OnModelCreating. Generated by CryptoSyncGenerator.");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine("    protected void SeedPermissions(ModelBuilder modelBuilder)");
        sb.AppendLine("    {");
        sb.AppendLine("        modelBuilder.Entity<SyncPermission>().HasData(");

        for (var i = 0; i < allPermissions.Count; i++)
        {
            var perm = allPermissions[i];
            var diffJson = BuildPermissionDiffJson(perm);
            // Deterministic GUID from role + table name
            byte[] guidBytes;
            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                guidBytes = md5.ComputeHash(Encoding.UTF8.GetBytes($"SyncPermission:{perm.Role}:{perm.TableName}"));
            }
            var guid = new Guid(guidBytes);

            sb.Append($"            new SyncPermission {{ Id = System.Guid.Parse(\"{guid}\"), ");
            sb.Append($"Role = (SyncRole){perm.Role}, ");
            sb.Append($"TableName = \"{perm.TableName}\", ");
            sb.Append($"PermissionDiffJson = @\"{diffJson.Replace("\"", "\"\"")}\"");
            sb.Append(" }");
            sb.AppendLine(i < allPermissions.Count - 1 ? "," : "");
        }

        sb.AppendLine("        );");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        return sb.ToString();
    }

    private static string BuildPermissionDiffJson(PermissionInfo perm)
    {
        if (perm.Access == "readwrite" && perm.ReadWriteColumns is null)
        {
            return "{}";
        }

        var parts = new List<string>();

        if (perm.Access == "readonly")
        {
            parts.Add($"\"{perm.TableName}\":\"readonly\"");
        }

        if (perm.ReadWriteColumns is not null)
        {
            foreach (var col in perm.ReadWriteColumns)
            {
                parts.Add($"\"{perm.TableName}.{col}\":\"readwrite\"");
            }
        }

        return "{" + string.Join(",", parts) + "}";
    }

    // ============================================================
    // DATA MODELS
    // ============================================================

    private sealed record EntityInfo(string Name, string Namespace, string DbSetName, List<PropertyInfo> Properties, List<PermissionInfo> Permissions);
    private sealed record PropertyInfo(string Name, string Type);
    private sealed record PermissionInfo(int Role, string Access, string TableName, string[]? ReadWriteColumns);
}
