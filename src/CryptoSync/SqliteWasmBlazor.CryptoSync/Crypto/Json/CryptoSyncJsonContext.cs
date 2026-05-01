using System.Text.Json;
using System.Text.Json.Serialization;
using SqliteWasmBlazor.Crypto.Abstractions.Models;

namespace SqliteWasmBlazor.CryptoSync.Crypto.Json;

/// <summary>
/// Source-generated JSON serialization context for CryptoSync-plane crypto
/// types layered on top of the base <c>SharedJsonContext</c> (PRF / VFS).
/// </summary>
[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(PushSendResult))]
public partial class CryptoSyncJsonContext : JsonSerializerContext;
