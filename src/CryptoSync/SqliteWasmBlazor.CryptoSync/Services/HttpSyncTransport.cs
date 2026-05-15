using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// HTTP impl of <see cref="ISyncTransport"/> against the whitelist-broadcast
/// delta relay (PHP entry point under <c>/DeltaRelay/</c>; see
/// <c>docs/security/relay-whitelist-design.md</c>). Wire shape:
///
/// <list type="bullet">
///   <item>
///     <c>POST /api/delta</c> — body <c>{"envelope":"base64"}</c>, headers
///     <c>X-Timestamp</c> + <c>X-Sender-PubKey</c> + <c>X-Sender-Sig</c>.
///     Sig is over <c>"deltapost-v1|" + ts + "|" + sha256(envelope) hex</c>.
///     The relay verifies the sender's pubkey hash is <c>active</c> on the
///     whitelist before accepting.
///   </item>
///   <item>
///     <c>GET /api/delta?since=CURSOR&amp;pubkey=PK</c> with headers
///     <c>X-Timestamp</c> + <c>X-Sig</c>. Sig is over
///     <c>"deltaget-v1|" + ts + "|" + pubkey</c>. The relay allows the GET
///     when the puller's pubkey hash is <c>active</c> OR <c>revoked</c> within
///     <c>read_grace_seconds</c> (graceful-handoff window).
///   </item>
/// </list>
///
/// <para>
/// <b>Cursor state.</b> Persisted via <see cref="IReceiveCursorStore"/> —
/// <see cref="InMemoryReceiveCursorStore"/> is the default (tests + dev);
/// production hosts swap in an OPFS-backed impl so a reload doesn't
/// replay envelopes. Refilled envelopes drain through <see cref="_buffer"/>
/// one at a time per <see cref="ISyncTransport.TryReceiveAsync"/> call.
/// </para>
/// </summary>
internal sealed class HttpSyncTransport(
    HttpClient httpClient,
    Uri relayBaseUri,
    ISenderAuthSigner senderSigner,
    IReceiveAuthSigner receiveSigner,
    IReceiveCursorStore? cursorStore = null) : ISyncTransport
{
    private readonly Queue<byte[]> _buffer = new();
    private readonly IReceiveCursorStore _cursorStore = cursorStore ?? new InMemoryReceiveCursorStore();
    private long? _cachedCursor;

    /// <summary>
    /// Set to <c>true</c> by the most recent <see cref="TryReceiveAsync"/>
    /// refill when the relay reports <c>"gc_requested": true</c> on the GET
    /// response — meaning the unpinned-row count has crossed the
    /// deployment's <c>gc_threshold_rows</c>. Purely informational: only
    /// the deployment admin can act on it (by publishing a fresh pinned
    /// seed via <see cref="IAdminPinService"/>). Non-admin transports
    /// observe the flag for diagnostics but ignore it operationally.
    /// </summary>
    public bool LastReceiveSignalledGcRequested { get; private set; }

    public async ValueTask SendAsync(
        byte[] envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var endpoint = new Uri(relayBaseUri, "api/delta");

        var timestamp = DateTimeOffset.UtcNow
            .ToUnixTimeSeconds()
            .ToString(CultureInfo.InvariantCulture);
        var envelopeHashHex = Convert.ToHexString(SHA256.HashData(envelope))
            .ToLowerInvariant();
        var senderPub = senderSigner.OwnEd25519PublicKeyBase64;
        var signature = await senderSigner
            .SignSendChallengeAsync(
                $"deltapost-v1|{timestamp}|{envelopeHashHex}", cancellationToken)
            .ConfigureAwait(false);

        var body = new SyncTransportDtos.SendRequest
        {
            Envelope = Convert.ToBase64String(envelope),
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(body, SyncTransportJsonContext.Default.SendRequest),
        };
        request.Headers.Add("X-Timestamp", timestamp);
        request.Headers.Add("X-Sender-PubKey", senderPub);
        request.Headers.Add("X-Sender-Sig", signature);

        using var response = await httpClient
            .SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public async ValueTask<byte[]?> TryReceiveAsync(CancellationToken cancellationToken = default)
    {
        if (_buffer.Count > 0)
        {
            return _buffer.Dequeue();
        }

        await RefillAsync(cancellationToken).ConfigureAwait(false);

        return _buffer.Count > 0 ? _buffer.Dequeue() : null;
    }

    private async Task RefillAsync(CancellationToken cancellationToken)
    {
        var timestamp = DateTimeOffset.UtcNow
            .ToUnixTimeSeconds()
            .ToString(CultureInfo.InvariantCulture);
        var pubKey = receiveSigner.OwnEd25519PublicKeyBase64;
        var signature = await receiveSigner
            .SignReceiveChallengeAsync(
                $"deltaget-v1|{timestamp}|{pubKey}", cancellationToken)
            .ConfigureAwait(false);

        var cursor = _cachedCursor ??= await _cursorStore.LoadAsync(cancellationToken).ConfigureAwait(false);

        var endpoint = new Uri(
            relayBaseUri,
            $"api/delta?since={cursor.ToString(CultureInfo.InvariantCulture)}"
            + $"&pubkey={Uri.EscapeDataString(pubKey)}");

        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Add("X-Timestamp", timestamp);
        request.Headers.Add("X-Sig", signature);

        using var response = await httpClient
            .SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var dto = await response.Content
            .ReadFromJsonAsync(
                SyncTransportJsonContext.Default.ReceiveResponse,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "HttpSyncTransport: empty response body from delta relay");

        LastReceiveSignalledGcRequested = dto.GcRequested;

        var newCursor = cursor;
        foreach (var item in dto.Envelopes)
        {
            _buffer.Enqueue(Convert.FromBase64String(item.Envelope));
            if (item.Cursor > newCursor)
            {
                newCursor = item.Cursor;
            }
        }

        if (dto.Cursor > newCursor)
        {
            newCursor = dto.Cursor;
        }

        if (newCursor != cursor)
        {
            _cachedCursor = newCursor;
            await _cursorStore.SaveAsync(newCursor, cancellationToken).ConfigureAwait(false);
        }
    }
}

internal static class SyncTransportDtos
{
    public sealed class SendRequest
    {
        [JsonPropertyName("envelope")]
        public required string Envelope { get; init; }
    }

    public sealed class ReceiveResponse
    {
        [JsonPropertyName("cursor")]
        public long Cursor { get; init; }

        [JsonPropertyName("envelopes")]
        public ReceiveEnvelope[] Envelopes { get; init; } = [];

        [JsonPropertyName("gc_requested")]
        public bool GcRequested { get; init; }
    }

    public sealed class ReceiveEnvelope
    {
        [JsonPropertyName("cursor")]
        public long Cursor { get; init; }

        [JsonPropertyName("envelope")]
        public required string Envelope { get; init; }
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(SyncTransportDtos.SendRequest))]
[JsonSerializable(typeof(SyncTransportDtos.ReceiveResponse))]
internal partial class SyncTransportJsonContext : JsonSerializerContext;
