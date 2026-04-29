using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// Admin-side client for the relay's <c>POST /api/whitelist</c> endpoint.
/// Builds the canonical signing string (lex-sorted by pubkey hash, see
/// <c>docs/security/relay-whitelist-design.md</c> §6.1), signs it via
/// <see cref="DeclarationSigner"/> with the admin's Ed25519 priv, and POSTs
/// the result. The relay verifies the signature, the admin's pubkey-hash
/// against its hardwired <c>admin_pubkey_hash</c>, and that <paramref
/// name="version"/> exceeds <c>current_version</c>; on success it
/// atomically replaces the whitelist contents.
///
/// <para>
/// Members are supplied <b>already-hashed</b> as
/// <c>sha256(deployment_salt || pubkey)</c>. Hashing happens at the call
/// site (the salt is per-deployment config; the caller — Step 4 hooks like
/// invitation promotion — already has it). The admin pubkey itself is sent
/// raw because the relay hashes it server-side to match config.
/// </para>
///
/// <para>
/// <b>Replay defense.</b> A push with a <paramref name="version"/> not
/// strictly greater than the relay's current value surfaces as a
/// <see cref="WhitelistVersionConflictException"/>; the relay's reported
/// <c>current_version</c> is included so callers can choose to retry with
/// <c>current + 1</c>.
/// </para>
/// </summary>
public sealed class WhitelistPushService(
    HttpClient httpClient,
    Uri relayBaseUri,
    DeclarationSigner signer)
{
    public async ValueTask<WhitelistPushResult> PushAsync(
        IReadOnlyList<WhitelistMember> members,
        string adminEd25519PublicKeyBase64,
        ReadOnlyMemory<byte> adminEd25519PrivateKey,
        long version,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(adminEd25519PublicKeyBase64);
        if (version <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(version), version, "version must be positive");
        }
        foreach (var m in members)
        {
            if (m.Status == WhitelistStatus.Revoked && m.RevokedAt is null)
            {
                throw new ArgumentException(
                    $"WhitelistMember '{m.PubkeyHash}' has Status=Revoked but no RevokedAt timestamp.",
                    nameof(members));
            }
        }

        var signature = await signer
            .SignWhitelistPushAsync(adminEd25519PrivateKey, version, members)
            .ConfigureAwait(false);

        var body = new WhitelistPushDto.Request
        {
            Version = version,
            Members = [.. members.Select(WhitelistPushDto.WireMember.From)],
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
        return new WhitelistPushResult(ok.Version, ok.MemberCount);
    }
}

/// <summary>
/// One row in a whitelist push. <see cref="PubkeyHash"/> is the lowercase
/// hex of <c>sha256(deployment_salt || pubkey_bytes)</c>; the relay never
/// sees the raw pubkey for non-admin members.
/// </summary>
public sealed record WhitelistMember(
    string PubkeyHash,
    WhitelistStatus Status,
    long? RevokedAt = null);

public enum WhitelistStatus
{
    Active,
    Revoked,
}

internal static class WhitelistStatusTokens
{
    public const string Active = "active";
    public const string Revoked = "revoked";

    public static string ToWire(WhitelistStatus status) => status switch
    {
        WhitelistStatus.Active => Active,
        WhitelistStatus.Revoked => Revoked,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };
}

public sealed record WhitelistPushResult(long Version, int MemberCount);

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

        [JsonPropertyName("members")]
        public required WireMember[] Members { get; init; }

        [JsonPropertyName("admin_pubkey")]
        public required string AdminPubkey { get; init; }

        [JsonPropertyName("admin_signature")]
        public required string AdminSignature { get; init; }
    }

    public sealed class WireMember
    {
        [JsonPropertyName("pubkey_hash")]
        public required string PubkeyHash { get; init; }

        [JsonPropertyName("status")]
        public required string Status { get; init; }

        [JsonPropertyName("revoked_at")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public long? RevokedAt { get; init; }

        public static WireMember From(WhitelistMember m) => new()
        {
            PubkeyHash = m.PubkeyHash,
            Status = WhitelistStatusTokens.ToWire(m.Status),
            RevokedAt = m.Status == WhitelistStatus.Revoked ? m.RevokedAt : null,
        };
    }

    public sealed class SuccessResponse
    {
        [JsonPropertyName("version")]
        public long Version { get; init; }

        [JsonPropertyName("member_count")]
        public int MemberCount { get; init; }
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
