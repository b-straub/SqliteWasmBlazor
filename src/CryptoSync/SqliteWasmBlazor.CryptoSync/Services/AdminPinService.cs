using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// Admin-side client for publishing a <i>pinned</i> envelope through
/// <c>POST /api/delta</c>. Pinned envelopes survive time-based GC and act
/// as the canonical baseline (seed) for the encrypted-delta stream — every
/// receiver pulling from <c>since=0</c> after the pin sees this envelope
/// first.
///
/// <para>
/// <b>Reseed semantics.</b> Each successful pin POST atomically purges all
/// prior rows (pinned + unpinned) from <c>deltas</c> on the relay, since by
/// definition every delta before the new seed is orphaned — no receiver
/// replays anything before the new pin. The relay's <c>AUTOINCREMENT</c>
/// counter survives the purge so cursor numbering stays monotonic and
/// existing receivers' <c>since=N</c> keeps working: their next GET picks
/// up the new pin at a fresh higher cursor.
/// </para>
///
/// <para>
/// <b>Authority.</b> The admin signs twice: once as a sender
/// (<c>deltapost-v1</c>, exactly like any other whitelisted client) and
/// once as the deployment admin (<c>deltapin-v1</c>) carried in
/// <c>X-Admin-Pin-Sig</c>. The relay accepts the pin only when the sender
/// pubkey hash equals the deployment <c>admin_pubkey_hash</c>, so a
/// non-admin client cannot escalate via a forged header.
/// </para>
///
/// <para>
/// <b>Wire shape.</b> Identical to a regular <c>POST /api/delta</c> plus
/// one extra header: <c>X-Admin-Pin-Sig: base64(Ed25519 sig over
/// "deltapin-v1|ts|sha256(envelope) hex")</c>. The 200 response carries
/// the assigned cursor plus <c>{"pinned": true, "prior_rows_purged": N}</c>
/// so callers can monitor seed-replacement scope.
/// </para>
/// </summary>
public interface IAdminPinService
{
    /// <summary>
    /// Publish <paramref name="envelope"/> as the new pinned baseline.
    /// Returns the relay-assigned cursor and the count of prior rows the
    /// relay purged in the same transaction.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Relay rejected the request (non-200) — message includes the status
    /// code. The most common cases are 403 (sender ≠ deployment admin) and
    /// 401 (pin sig didn't verify, or sender's own POST sig failed).
    /// </exception>
    ValueTask<AdminPinResult> PinAsync(
        byte[] envelope,
        string adminEd25519PublicKeyBase64,
        ReadOnlyMemory<byte> adminEd25519PrivateKey,
        ISenderAuthSigner senderSigner,
        CancellationToken cancellationToken = default);
}

internal sealed class AdminPinService(
    HttpClient httpClient,
    Uri relayBaseUri,
    DeclarationSigner signer) : IAdminPinService
{
    public async ValueTask<AdminPinResult> PinAsync(
        byte[] envelope,
        string adminEd25519PublicKeyBase64,
        ReadOnlyMemory<byte> adminEd25519PrivateKey,
        ISenderAuthSigner senderSigner,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(adminEd25519PublicKeyBase64);
        ArgumentNullException.ThrowIfNull(senderSigner);
        if (envelope.Length == 0)
        {
            throw new ArgumentException("envelope must be non-empty.", nameof(envelope));
        }
        if (!string.Equals(
                senderSigner.OwnEd25519PublicKeyBase64,
                adminEd25519PublicKeyBase64,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "AdminPinService: senderSigner pubkey does not match the supplied admin pubkey. "
                + "The relay requires the sender to be the deployment admin for pin POSTs.",
                nameof(senderSigner));
        }

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var timestampStr = timestamp.ToString(CultureInfo.InvariantCulture);
        var envelopeHashHex = Convert
            .ToHexString(SHA256.HashData(envelope))
            .ToLowerInvariant();

        var senderSigBase64 = await senderSigner
            .SignSendChallengeAsync(
                $"deltapost-v1|{timestampStr}|{envelopeHashHex}", cancellationToken)
            .ConfigureAwait(false);
        var pinSigBytes = await signer
            .SignDeltaPinAsync(adminEd25519PrivateKey, timestamp, envelopeHashHex)
            .ConfigureAwait(false);

        var endpoint = new Uri(relayBaseUri, "api/delta");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(
                new AdminPinDto.Request { Envelope = Convert.ToBase64String(envelope) },
                AdminPinJsonContext.Default.Request),
        };
        request.Headers.Add("X-Timestamp", timestampStr);
        request.Headers.Add("X-Sender-PubKey", adminEd25519PublicKeyBase64);
        request.Headers.Add("X-Sender-Sig", senderSigBase64);
        request.Headers.Add("X-Admin-Pin-Sig", Convert.ToBase64String(pinSigBytes));

        using var response = await httpClient
            .SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var dto = await response.Content
            .ReadFromJsonAsync(
                AdminPinJsonContext.Default.SuccessResponse, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "AdminPinService: empty 200 body from delta relay");
        if (!dto.Pinned)
        {
            throw new InvalidOperationException(
                "AdminPinService: relay accepted POST but did not flag the row as pinned — "
                + "deployment misconfiguration or unrecognized X-Admin-Pin-Sig path.");
        }
        return new AdminPinResult(dto.Cursor, dto.PriorRowsPurged);
    }
}

/// <summary>
/// Result of a successful pin POST: the relay-assigned cursor for the new
/// seed and the count of prior rows in <c>deltas</c> the relay purged
/// atomically as part of the reseed.
/// </summary>
public sealed record AdminPinResult(long Cursor, int PriorRowsPurged);

internal static class AdminPinDto
{
    public sealed class Request
    {
        [JsonPropertyName("envelope")]
        public required string Envelope { get; init; }
    }

    public sealed class SuccessResponse
    {
        [JsonPropertyName("cursor")]
        public long Cursor { get; init; }

        [JsonPropertyName("pinned")]
        public bool Pinned { get; init; }

        [JsonPropertyName("prior_rows_purged")]
        public int PriorRowsPurged { get; init; }
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(AdminPinDto.Request))]
[JsonSerializable(typeof(AdminPinDto.SuccessResponse))]
internal partial class AdminPinJsonContext : JsonSerializerContext;
