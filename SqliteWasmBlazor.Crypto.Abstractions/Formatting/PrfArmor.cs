using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SqliteWasmBlazor.Crypto.Abstractions.Formatting;

/// <summary>
/// Provides PGP-style ASCII armor formatting for PRF keys and messages.
/// Uses "PFA" (PRF Authenticated) instead of "PGP".
/// Metadata is embedded in the Base64 payload (like PGP), not in armor headers.
/// </summary>
public static partial class PrfArmor
{
    /// <summary>Well-known mail subject for encrypted messages.</summary>
    public const string SubjectEncryptedMessage = "Encrypted Message";

    /// <summary>Well-known mail subject for invitations.</summary>
    public const string SubjectInvitation = "PRF Invitation";

    /// <summary>Well-known mail subject for invite responses.</summary>
    public const string SubjectInviteResponse = "PRF Invite Response";

    private const string PublicKeyHeader = "-----BEGIN PFA PUBLIC KEY-----";
    private const string PublicKeyFooter = "-----END PFA PUBLIC KEY-----";
    private const string PrivateKeyHeader = "-----BEGIN PFA PRIVATE KEY-----";
    private const string PrivateKeyFooter = "-----END PFA PRIVATE KEY-----";
    private const string MessageHeader = "-----BEGIN PFA MESSAGE-----";
    private const string MessageFooter = "-----END PFA MESSAGE-----";
    private const string SignedInviteHeader = "-----BEGIN PFA SIGNED INVITE-----";
    private const string SignedInviteFooter = "-----END PFA SIGNED INVITE-----";
    private const string SignedResponseHeader = "-----BEGIN PFA SIGNED RESPONSE-----";
    private const string SignedResponseFooter = "-----END PFA SIGNED RESPONSE-----";
    private const string SignedMessageHeader = "-----BEGIN PFA SIGNED MESSAGE-----";
    private const string SignedMessageFooter = "-----END PFA SIGNED MESSAGE-----";
    private const int LineLength = 64;
    private const int FormatVersion = 1;

    /// <summary>
    /// Formats a Base64 public key in PGP-style armor with optional metadata.
    /// Metadata is embedded in the payload, not armor headers.
    /// </summary>
    public static string ArmorPublicKey(string base64PublicKey, PublicKeyMetadata? metadata = null)
    {
        var payload = new PublicKeyPayload
        {
            Version = FormatVersion,
            Key = base64PublicKey,
            Name = metadata?.Name,
            Email = metadata?.Email,
            Comment = metadata?.Comment,
            Created = metadata?.Created?.ToString("yyyy-MM-dd")
        };

        var json = JsonSerializer.Serialize(payload, PayloadJsonContext.Default.PublicKeyPayload);
        var base64Payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        var wrapped = WrapBase64(base64Payload);

        return $"{PublicKeyHeader}\n\n{wrapped}\n{PublicKeyFooter}";
    }

    /// <summary>
    /// Formats a Base64 private key in PGP-style armor.
    /// WARNING: Private keys should be stored securely!
    /// </summary>
    public static string ArmorPrivateKey(string base64PrivateKey, PublicKeyMetadata? metadata = null)
    {
        var payload = new PublicKeyPayload
        {
            Version = FormatVersion,
            Key = base64PrivateKey,
            Name = metadata?.Name,
            Email = metadata?.Email,
            Comment = metadata?.Comment,
            Created = metadata?.Created?.ToString("yyyy-MM-dd")
        };

        var json = JsonSerializer.Serialize(payload, PayloadJsonContext.Default.PublicKeyPayload);
        var base64Payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        var wrapped = WrapBase64(base64Payload);

        return $"{PrivateKeyHeader}\n\n{wrapped}\n{PrivateKeyFooter}";
    }

    /// <summary>
    /// Extracts a Base64 public key from PGP-style armor.
    /// Supports both new format (with embedded metadata) and legacy raw Base64.
    /// </summary>
    public static string? UnArmorPublicKey(string armored)
    {
        var (key, _) = UnArmorPublicKeyWithMetadata(armored);
        return key;
    }

    /// <summary>
    /// Extracts a Base64 private key from PGP-style armor.
    /// </summary>
    public static string? UnArmorPrivateKey(string armored)
    {
        var (key, _) = UnArmorKeyWithMetadata(armored, PrivateKeyHeader, PrivateKeyFooter);
        return key;
    }

    /// <summary>
    /// Extracts a Base64 public key and metadata from PGP-style armor.
    /// </summary>
    public static (string? Base64Key, PublicKeyMetadata? Metadata) UnArmorPublicKeyWithMetadata(string armored)
    {
        return UnArmorKeyWithMetadata(armored, PublicKeyHeader, PublicKeyFooter);
    }

    /// <summary>
    /// Extracts a Base64 private key and metadata from PGP-style armor.
    /// </summary>
    public static (string? Base64Key, PublicKeyMetadata? Metadata) UnArmorPrivateKeyWithMetadata(string armored)
    {
        return UnArmorKeyWithMetadata(armored, PrivateKeyHeader, PrivateKeyFooter);
    }

    private static (string? Base64Key, PublicKeyMetadata? Metadata) UnArmorKeyWithMetadata(
        string armored, string header, string footer)
    {
        var base64 = ExtractBase64(armored, header, footer);
        if (base64 is null)
        {
            return (null, null);
        }

        try
        {
            var bytes = Convert.FromBase64String(base64);
            var json = Encoding.UTF8.GetString(bytes);

            if (json.TrimStart().StartsWith('{'))
            {
                var payload = JsonSerializer.Deserialize(json, PayloadJsonContext.Default.PublicKeyPayload);
                if (payload?.Key is not null)
                {
                    var metadata = (payload.Name is not null || payload.Email is not null ||
                                   payload.Comment is not null || payload.Created is not null)
                        ? new PublicKeyMetadata
                        {
                            Name = payload.Name,
                            Email = payload.Email,
                            Comment = payload.Comment,
                            Created = DateOnly.TryParse(payload.Created, out var date) ? date : null
                        }
                        : null;

                    return (payload.Key, metadata);
                }
            }
        }
        catch
        {
            // Not JSON, fall through to legacy handling
        }

        // Legacy format: raw Base64 key without metadata
        return (base64, null);
    }

    /// <summary>
    /// Formats an encrypted message JSON in PGP-style armor.
    /// </summary>
    public static string ArmorMessage(string messageJson)
    {
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(messageJson));
        var wrapped = WrapBase64(base64);
        return $"{MessageHeader}\n\n{wrapped}\n{MessageFooter}";
    }

    /// <summary>
    /// Extracts and decodes a message JSON from PGP-style armor.
    /// </summary>
    public static string? UnArmorMessage(string armored)
    {
        var base64 = ExtractBase64(armored, MessageHeader, MessageFooter);
        if (base64 is null)
        {
            return null;
        }

        try
        {
            var bytes = Convert.FromBase64String(base64);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Parses an EncryptedMessage from armored text or raw JSON.
    /// This is the SSOT for parsing encrypted messages.
    /// </summary>
    /// <param name="input">Armored PFA message or raw JSON</param>
    /// <returns>Parsed EncryptedMessage or null if parsing fails</returns>
    public static Models.AsymmetricEncryptedData? ParseEncryptedMessage(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        string? json;
        if (IsArmoredMessage(input))
        {
            json = UnArmorMessage(input);
            if (json is null)
            {
                return null;
            }
        }
        else
        {
            json = input.Trim();
        }

        try
        {
            return JsonSerializer.Deserialize(json, Json.SharedJsonContext.Default.AsymmetricEncryptedData);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Extracts and parses an EncryptedMessage from text that may contain a PFA MESSAGE block.
    /// Searches for armor markers within the text.
    /// </summary>
    /// <param name="text">Text that may contain an embedded PFA MESSAGE block</param>
    /// <returns>Parsed EncryptedMessage or null if no valid message found</returns>
    public static Models.AsymmetricEncryptedData? ExtractEncryptedMessage(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var headerIndex = text.IndexOf(MessageHeader, StringComparison.Ordinal);
        var footerIndex = text.IndexOf(MessageFooter, StringComparison.Ordinal);

        if (headerIndex < 0 || footerIndex < 0 || footerIndex <= headerIndex)
        {
            return null;
        }

        var armored = text.Substring(headerIndex, footerIndex - headerIndex + MessageFooter.Length);
        return ParseEncryptedMessage(armored);
    }

    /// <summary>
    /// Parses a SymmetricEncryptedMessage from armored text or raw JSON.
    /// This is the SSOT for parsing symmetric encrypted messages.
    /// </summary>
    /// <param name="input">Armored PFA message or raw JSON</param>
    /// <returns>Parsed SymmetricEncryptedMessage or null if parsing fails</returns>
    public static Models.SymmetricEncryptedData? ParseSymmetricEncryptedMessage(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        string? json;
        if (IsArmoredMessage(input))
        {
            json = UnArmorMessage(input);
            if (json is null)
            {
                return null;
            }
        }
        else
        {
            json = input.Trim();
        }

        try
        {
            return JsonSerializer.Deserialize(json, Json.SharedJsonContext.Default.SymmetricEncryptedData);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Checks if a string looks like an armored public key.
    /// </summary>
    public static bool IsArmoredPublicKey(string text)
    {
        return text.TrimStart().StartsWith(PublicKeyHeader, StringComparison.Ordinal);
    }

    /// <summary>
    /// Checks if a string looks like an armored private key.
    /// </summary>
    public static bool IsArmoredPrivateKey(string text)
    {
        return text.TrimStart().StartsWith(PrivateKeyHeader, StringComparison.Ordinal);
    }

    /// <summary>
    /// Checks if a string looks like an armored message.
    /// </summary>
    public static bool IsArmoredMessage(string text)
    {
        return text.TrimStart().StartsWith(MessageHeader, StringComparison.Ordinal);
    }

    /// <summary>
    /// Checks if a string looks like an armored signed invite.
    /// </summary>
    public static bool IsArmoredSignedInvite(string text)
    {
        return text.TrimStart().StartsWith(SignedInviteHeader, StringComparison.Ordinal);
    }

    /// <summary>
    /// Checks if a string looks like an armored signed response.
    /// </summary>
    public static bool IsArmoredSignedResponse(string text)
    {
        return text.TrimStart().StartsWith(SignedResponseHeader, StringComparison.Ordinal);
    }

    /// <summary>
    /// Formats a signed invite in PGP-style armor.
    /// </summary>
    public static string ArmorSignedInvite(string inviteJson)
    {
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(inviteJson));
        var wrapped = WrapBase64(base64);
        return $"{SignedInviteHeader}\n\n{wrapped}\n{SignedInviteFooter}";
    }

    /// <summary>
    /// Extracts and decodes a signed invite JSON from PGP-style armor.
    /// </summary>
    public static string? UnArmorSignedInvite(string armored)
    {
        var base64 = ExtractBase64(armored, SignedInviteHeader, SignedInviteFooter);
        if (base64 is null)
        {
            return null;
        }

        try
        {
            var bytes = Convert.FromBase64String(base64);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Formats a signed response in PGP-style armor.
    /// </summary>
    public static string ArmorSignedResponse(string responseJson)
    {
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(responseJson));
        var wrapped = WrapBase64(base64);
        return $"{SignedResponseHeader}\n\n{wrapped}\n{SignedResponseFooter}";
    }

    /// <summary>
    /// Extracts and decodes a signed response JSON from PGP-style armor.
    /// </summary>
    public static string? UnArmorSignedResponse(string armored)
    {
        var base64 = ExtractBase64(armored, SignedResponseHeader, SignedResponseFooter);
        if (base64 is null)
        {
            return null;
        }

        try
        {
            var bytes = Convert.FromBase64String(base64);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Checks if a string looks like an armored signed message.
    /// </summary>
    public static bool IsArmoredSignedMessage(string text)
    {
        return text.TrimStart().StartsWith(SignedMessageHeader, StringComparison.Ordinal);
    }

    /// <summary>
    /// Formats a signed message in PGP-style armor.
    /// </summary>
    public static string ArmorSignedMessage(string signedMessageJson)
    {
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(signedMessageJson));
        var wrapped = WrapBase64(base64);
        return $"{SignedMessageHeader}\n\n{wrapped}\n{SignedMessageFooter}";
    }

    /// <summary>
    /// Extracts and decodes a signed message JSON from PGP-style armor.
    /// </summary>
    public static string? UnArmorSignedMessage(string armored)
    {
        var base64 = ExtractBase64(armored, SignedMessageHeader, SignedMessageFooter);
        if (base64 is null)
        {
            return null;
        }

        try
        {
            var bytes = Convert.FromBase64String(base64);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return null;
        }
    }

    private static string WrapBase64(string base64)
    {
        var lines = new List<string>();
        for (var i = 0; i < base64.Length; i += LineLength)
        {
            var length = Math.Min(LineLength, base64.Length - i);
            lines.Add(base64.Substring(i, length));
        }
        return string.Join("\n", lines);
    }

    private static string? ExtractBase64(string armored, string header, string footer)
    {
        var headerIndex = armored.IndexOf(header, StringComparison.Ordinal);
        var footerIndex = armored.IndexOf(footer, StringComparison.Ordinal);

        if (headerIndex < 0 || footerIndex < 0 || footerIndex <= headerIndex)
        {
            return null;
        }

        var content = armored[(headerIndex + header.Length)..footerIndex];
        return new string(content.Where(c => !char.IsWhiteSpace(c)).ToArray());
    }

    /// <summary>
    /// Internal payload format for keys with embedded metadata.
    /// </summary>
    private sealed class PublicKeyPayload
    {
        [JsonPropertyName("v")]
        public int Version { get; init; }

        [JsonPropertyName("k")]
        public string? Key { get; init; }

        [JsonPropertyName("n")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Name { get; init; }

        [JsonPropertyName("e")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Email { get; init; }

        [JsonPropertyName("c")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Comment { get; init; }

        [JsonPropertyName("d")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Created { get; init; }
    }

    [JsonSourceGenerationOptions(
        JsonSerializerDefaults.Web,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(PublicKeyPayload))]
    private partial class PayloadJsonContext : JsonSerializerContext;
}

/// <summary>
/// Optional metadata for a PFA key.
/// </summary>
public sealed class PublicKeyMetadata
{
    /// <summary>
    /// Display name of the key owner.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Email address of the key owner.
    /// </summary>
    public string? Email { get; init; }

    /// <summary>
    /// Optional comment or description.
    /// </summary>
    public string? Comment { get; init; }

    /// <summary>
    /// Date when the key was created.
    /// </summary>
    public DateOnly? Created { get; init; }
}
