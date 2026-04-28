using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// HTTP impl of <see cref="ISyncTransport"/> against the delta relay
/// (PHP entry point under <c>/DeltaRelay/</c>; see
/// <c>project_relay_design</c> memory). Wire shape:
///
/// <list type="bullet">
///   <item>
///     <c>POST /api/delta</c> — body
///     <c>{"recipientPublicKeys":["base64-Ed25519",...],"envelope":"base64"}</c>.
///     Unauthenticated; the relay can't read the payload and the V2 envelope
///     already provides confidentiality / integrity guarantees.
///   </item>
///   <item>
///     <c>GET /api/delta?recipient=PK&amp;since=CURSOR</c> with headers
///     <c>X-Timestamp</c> + <c>X-Sig</c> over <c>"{timestamp}|{recipient}"</c>.
///     Stateless — verifier key is the recipient pubkey from the query.
///     Stops a passive observer who learns the pubkey from draining the
///     inbox and learning metadata.
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
public sealed class HttpSyncTransport(
    HttpClient httpClient,
    Uri relayBaseUri,
    IReceiveAuthSigner authSigner,
    IReceiveCursorStore? cursorStore = null) : ISyncTransport
{
    private readonly Queue<byte[]> _buffer = new();
    private readonly IReceiveCursorStore _cursorStore = cursorStore ?? new InMemoryReceiveCursorStore();
    private long? _cachedCursor;

    public async ValueTask SendAsync(
        byte[] envelope,
        IReadOnlyList<string> recipientPublicKeys,
        CancellationToken cancellationToken = default)
    {
        if (recipientPublicKeys.Count == 0)
        {
            throw new ArgumentException(
                "recipientPublicKeys must not be empty",
                nameof(recipientPublicKeys));
        }

        var endpoint = new Uri(relayBaseUri, "api/delta");
        var body = new SyncTransportDtos.SendRequest
        {
            RecipientPublicKeys = [.. recipientPublicKeys],
            Envelope = Convert.ToBase64String(envelope),
        };

        using var response = await httpClient
            .PostAsJsonAsync(
                endpoint,
                body,
                SyncTransportJsonContext.Default.SendRequest,
                cancellationToken)
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
        var pubKey = authSigner.OwnEd25519PublicKeyBase64;
        var signature = await authSigner
            .SignReceiveChallengeAsync($"{timestamp}|{pubKey}", cancellationToken)
            .ConfigureAwait(false);

        var cursor = _cachedCursor ??= await _cursorStore.LoadAsync(cancellationToken).ConfigureAwait(false);

        var endpoint = new Uri(
            relayBaseUri,
            $"api/delta?recipient={Uri.EscapeDataString(pubKey)}"
            + $"&since={cursor.ToString(CultureInfo.InvariantCulture)}");

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
        [JsonPropertyName("recipientPublicKeys")]
        public required string[] RecipientPublicKeys { get; init; }

        [JsonPropertyName("envelope")]
        public required string Envelope { get; init; }
    }

    public sealed class ReceiveResponse
    {
        [JsonPropertyName("cursor")]
        public long Cursor { get; init; }

        [JsonPropertyName("envelopes")]
        public ReceiveEnvelope[] Envelopes { get; init; } = [];
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
