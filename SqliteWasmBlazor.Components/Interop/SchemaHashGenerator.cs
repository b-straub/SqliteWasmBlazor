using MessagePack;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace SqliteWasmBlazor.Components.Interop;

/// <summary>
/// Generates deterministic schema hashes for MessagePack-serialized types
/// Computes hash based on MessagePack [Key(n)] attributes, property names, and types
/// Used for automatic schema version validation without manual version management
/// </summary>
public static class SchemaHashGenerator
{
    /// <summary>
    /// Compute schema hash for a MessagePack-decorated type
    /// Hash is based on: Key indices, property names, and property types
    /// </summary>
    /// <typeparam name="T">MessagePack-decorated type with [Key] attributes</typeparam>
    /// <returns>16-character hex hash (first 64 bits of SHA256)</returns>
    public static string ComputeHash<T>()
    {
        return ComputeHash(typeof(T));
    }

    /// <summary>
    /// Compute schema hash for a MessagePack-decorated type
    /// </summary>
    /// <param name="type">MessagePack-decorated type with [Key] attributes</param>
    /// <returns>16-character hex hash (first 64 bits of SHA256)</returns>
    public static string ComputeHash(Type type)
    {
        // Get all properties with [Key(n)] attributes
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => new
            {
                Property = p,
                KeyAttr = p.GetCustomAttribute<KeyAttribute>()
            })
            .Where(x => x.KeyAttr is not null)
            .OrderBy(x => x.KeyAttr!.IntKey) // Sort by Key index for deterministic order
            .ToList();

        if (properties.Count == 0)
        {
            throw new InvalidOperationException(
                $"Type '{type.FullName}' has no properties with [Key] attributes. " +
                $"Cannot compute schema hash for non-MessagePack types.");
        }

        // Build deterministic schema string
        var schemaBuilder = new StringBuilder();
        schemaBuilder.Append(type.FullName ?? type.Name);
        schemaBuilder.Append('|');

        foreach (var prop in properties)
        {
            schemaBuilder.Append($"[{prop.KeyAttr!.IntKey}]");
            schemaBuilder.Append(prop.Property.Name);
            schemaBuilder.Append(':');
            schemaBuilder.Append(prop.Property.PropertyType.FullName ?? prop.Property.PropertyType.Name);
            schemaBuilder.Append('|');
        }

        var schemaString = schemaBuilder.ToString();

        // Compute SHA256 hash
        var bytes = Encoding.UTF8.GetBytes(schemaString);
        var hashBytes = SHA256.HashData(bytes);

        // Return first 64 bits (8 bytes) as hex string (16 chars)
        return Convert.ToHexString(hashBytes, 0, 8).ToLowerInvariant();
    }

    /// <summary>
    /// Get human-readable schema description for debugging
    /// </summary>
    public static string GetSchemaDescription<T>()
    {
        return GetSchemaDescription(typeof(T));
    }

    /// <summary>
    /// Get human-readable schema description for debugging
    /// </summary>
    public static string GetSchemaDescription(Type type)
    {
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => new
            {
                Property = p,
                KeyAttr = p.GetCustomAttribute<KeyAttribute>()
            })
            .Where(x => x.KeyAttr is not null)
            .OrderBy(x => x.KeyAttr!.IntKey)
            .ToList();

        var lines = new List<string>
        {
            $"Type: {type.FullName ?? type.Name}",
            $"Properties: {properties.Count}"
        };

        foreach (var prop in properties)
        {
            lines.Add($"  [{prop.KeyAttr!.IntKey}] {prop.Property.Name}: {prop.Property.PropertyType.Name}");
        }

        return string.Join(Environment.NewLine, lines);
    }
}
