using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// Admin-side client for the relay's <c>POST /api/whitelist</c> endpoint.
/// Pushes <i>incremental ops</i> (add / revoke) signed by the admin's
/// Ed25519 priv via <see cref="DeclarationSigner"/>; the relay verifies the
/// signature, the admin's pubkey-hash against its hardwired
/// <c>admin_pubkey_hash</c>, and that <paramref name="version"/> exceeds
/// <c>current_version</c>; on success it applies the ops in order under a
/// single transaction and bumps <c>current_version</c>.
///
/// <para>
/// Op semantics:
/// <list type="bullet">
///   <item><c>Add</c> — INSERT (or re-activate-on-conflict). The relay
///         clears any prior <c>revoked_at</c> and sets status to
///         <c>active</c>. Idempotent.</item>
///   <item><c>Revoke</c> — UPDATE <c>status='revoked'</c> +
///         <c>revoked_at=...</c>. No-op if the hash isn't on the
///         whitelist.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Pubkey hashing.</b> Members are referenced by
/// <c>sha256(deployment_salt || pubkey)</c> hex. Use
/// <see cref="HashPubkey"/> at the call site — the salt stays where the
/// pubkey originates (admin device config) so non-admin code paths never
/// see it.
/// </para>
///
/// <para>
/// <b>Replay defense.</b> A push with a <paramref name="version"/> not
/// strictly greater than the relay's current value surfaces as a
/// <see cref="WhitelistVersionConflictException"/>; the relay's reported
/// <c>current_version</c> is included so callers can retry at
/// <c>current + 1</c>.
/// </para>
/// </summary>
public interface IWhitelistPushService
{
    /// <summary>
    /// Push <paramref name="operations"/> to the relay's whitelist, signed
    /// by the admin's Ed25519 priv. Returns the relay's accepted version +
    /// op count, or throws <see cref="WhitelistVersionConflictException"/>
    /// on 409 (replay).
    /// </summary>
    ValueTask<WhitelistPushResult> PushAsync(
        IReadOnlyList<WhitelistOp> operations,
        string adminEd25519PublicKeyBase64,
        ReadOnlyMemory<byte> adminEd25519PrivateKey,
        long version,
        CancellationToken cancellationToken = default);
}

public sealed class WhitelistPushService(
    HttpClient httpClient,
    Uri relayBaseUri,
    DeclarationSigner signer) : IWhitelistPushService
{
    public async ValueTask<WhitelistPushResult> PushAsync(
        IReadOnlyList<WhitelistOp> operations,
        string adminEd25519PublicKeyBase64,
        ReadOnlyMemory<byte> adminEd25519PrivateKey,
        long version,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(adminEd25519PublicKeyBase64);
        if (operations.Count == 0)
        {
            throw new ArgumentException(
                "operations must be non-empty.", nameof(operations));
        }
        if (version <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(version), version, "version must be positive");
        }

        var signature = await signer
            .SignWhitelistOpsAsync(adminEd25519PrivateKey, version, operations)
            .ConfigureAwait(false);

        var body = new WhitelistPushDto.Request
        {
            Version = version,
            Operations = [.. operations.Select(WhitelistPushDto.WireOp.From)],
            AdminPubkey = adminEd25519PublicKeyBase64,
            AdminSignature = Convert.ToBase64String(signature),
        };

        var endpoint = new Uri(relayBaseUri, "api/whitelist");
        using var response = await httpClient
            .PostAsJsonAsync(
                endpoint,
                body,
                WhitelistPushJsonContext.Default.Request,
                cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            var conflict = await response.Content
                .ReadFromJsonAsync(
                    WhitelistPushJsonContext.Default.ConflictResponse,
                    cancellationToken)
                .ConfigureAwait(false);
            throw new WhitelistVersionConflictException(
                attemptedVersion: version,
                currentVersion: conflict?.CurrentVersion ?? -1);
        }

        response.EnsureSuccessStatusCode();

        var ok = await response.Content
            .ReadFromJsonAsync(
                WhitelistPushJsonContext.Default.SuccessResponse,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "WhitelistPushService: empty 200 body from delta relay");
        return new WhitelistPushResult(ok.Version, ok.OperationCount);
    }

    /// <summary>
    /// Produce the lowercase-hex of <c>sha256(deploymentSalt || pubkey)</c>
    /// — the form members are stored as on the relay. Salt stays at the
    /// call site (admin device config), so this static helper is safe to
    /// use anywhere a pubkey-hash is needed without DI plumbing.
    /// </summary>
    public static string HashPubkey(string deploymentSaltBase64, string ed25519PublicKeyBase64)
    {
        ArgumentNullException.ThrowIfNull(deploymentSaltBase64);
        ArgumentNullException.ThrowIfNull(ed25519PublicKeyBase64);

        var salt = Convert.FromBase64String(deploymentSaltBase64);
        var pubkey = Convert.FromBase64String(ed25519PublicKeyBase64);
        var buffer = new byte[salt.Length + pubkey.Length];
        Buffer.BlockCopy(salt, 0, buffer, 0, salt.Length);
        Buffer.BlockCopy(pubkey, 0, buffer, salt.Length, pubkey.Length);
        return Convert.ToHexString(SHA256.HashData(buffer)).ToLowerInvariant();
    }
}

/// <summary>
/// One operation in a whitelist push. Use <see cref="Add"/> /
/// <see cref="Revoke"/> factories rather than constructing directly.
/// </summary>
public abstract record WhitelistOp
{
    private WhitelistOp() { }

    public abstract string PubkeyHash { get; }

    public sealed record AddOp(string PubkeyHash) : WhitelistOp
    {
        public override string PubkeyHash { get; } = PubkeyHash;
    }

    public sealed record RevokeOp(string PubkeyHash, long RevokedAt) : WhitelistOp
    {
        public override string PubkeyHash { get; } = PubkeyHash;
    }

    public static AddOp Add(string pubkeyHash) => new(pubkeyHash);
    public static RevokeOp Revoke(string pubkeyHash, long revokedAt) => new(pubkeyHash, revokedAt);
}

public sealed record WhitelistPushResult(long Version, int OperationCount);

/// <summary>
/// The relay rejected a whitelist push because the supplied version was not
/// strictly greater than the relay's current version (replay-defense). The
/// included <see cref="CurrentVersion"/> is what the relay reports — useful
/// for callers that want to retry at <c>CurrentVersion + 1</c>.
/// </summary>
public sealed class WhitelistVersionConflictException(long attemptedVersion, long currentVersion)
    : InvalidOperationException(
        $"Relay rejected whitelist push: attempted version {attemptedVersion} is not greater than current_version {currentVersion}.")
{
    public long AttemptedVersion { get; } = attemptedVersion;
    public long CurrentVersion { get; } = currentVersion;
}

internal static class WhitelistPushDto
{
    public sealed class Request
    {
        [JsonPropertyName("version")]
        public required long Version { get; init; }

        [JsonPropertyName("operations")]
        public required WireOp[] Operations { get; init; }

        [JsonPropertyName("admin_pubkey")]
        public required string AdminPubkey { get; init; }

        [JsonPropertyName("admin_signature")]
        public required string AdminSignature { get; init; }
    }

    public sealed class WireOp
    {
        [JsonPropertyName("op")]
        public required string Op { get; init; }

        [JsonPropertyName("pubkey_hash")]
        public required string PubkeyHash { get; init; }

        [JsonPropertyName("revoked_at")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public long? RevokedAt { get; init; }

        public static WireOp From(WhitelistOp op) => op switch
        {
            WhitelistOp.AddOp a => new WireOp
            {
                Op = "add",
                PubkeyHash = a.PubkeyHash,
                RevokedAt = null,
            },
            WhitelistOp.RevokeOp r => new WireOp
            {
                Op = "revoke",
                PubkeyHash = r.PubkeyHash,
                RevokedAt = r.RevokedAt,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(op), op?.GetType(), null),
        };
    }

    public sealed class SuccessResponse
    {
        [JsonPropertyName("version")]
        public long Version { get; init; }

        [JsonPropertyName("operation_count")]
        public int OperationCount { get; init; }
    }

    public sealed class ConflictResponse
    {
        [JsonPropertyName("error")]
        public string? Error { get; init; }

        [JsonPropertyName("current_version")]
        public long CurrentVersion { get; init; }
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(WhitelistPushDto.Request))]
[JsonSerializable(typeof(WhitelistPushDto.SuccessResponse))]
[JsonSerializable(typeof(WhitelistPushDto.ConflictResponse))]
internal partial class WhitelistPushJsonContext : JsonSerializerContext;
