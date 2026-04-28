using System.Net;
using System.Text;
using System.Text.Json;
using SqliteWasmBlazor.CryptoSync.Tests.Fixtures;
using Xunit;

namespace SqliteWasmBlazor.CryptoSync.Tests;

/// <summary>
/// Wire-shape coverage for <see cref="HttpSyncTransport"/> against the
/// delta-relay HTTP contract documented in <c>project_relay_design</c>.
/// Asserts request layout (POST body, GET query, signed headers) and
/// receive-side cursor advancement / buffer drain. Real Ed25519 round-trips
/// belong in an integration test against the live PHP relay; these tests
/// stay in-process.
/// </summary>
public class HttpSyncTransportTests
{
    private const string AlicePub = "QUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUE="; // base64 of 32 'A' bytes
    private static readonly Uri RelayBase = new("http://delta-relay.test/");

    [Fact]
    public async Task SendAsync_PostsExpectedBodyToDeltaEndpoint()
    {
        var handler = new StubHttpMessageHandler();
        using var client = new HttpClient(handler);
        var transport = new HttpSyncTransport(client, RelayBase, NewSigner());

        await transport.SendAsync(
            envelope: [0xDE, 0xAD, 0xBE, 0xEF],
            recipientPublicKeys: ["bob-pub", "carol-pub"]);

        var req = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("http://delta-relay.test/api/delta", req.RequestUri.ToString());

        using var doc = JsonDocument.Parse(req.Body!);
        var recipients = doc.RootElement.GetProperty("recipientPublicKeys")
            .EnumerateArray()
            .Select(e => e.GetString() ?? throw new InvalidOperationException("recipient was null"))
            .ToArray();
        Assert.Equal(["bob-pub", "carol-pub"], recipients);

        var envelopeB64 = doc.RootElement.GetProperty("envelope").GetString();
        Assert.Equal(Convert.ToBase64String([0xDE, 0xAD, 0xBE, 0xEF]), envelopeB64);
    }

    [Fact]
    public async Task SendAsync_EmptyRecipients_ThrowsArgumentExceptionWithoutHttpCall()
    {
        var handler = new StubHttpMessageHandler();
        using var client = new HttpClient(handler);
        var transport = new HttpSyncTransport(client, RelayBase, NewSigner());

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await transport.SendAsync([0x01], Array.Empty<string>()));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SendAsync_NonSuccessStatus_Throws()
    {
        var handler = new StubHttpMessageHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("{\"error\":\"bad\"}")
            }
        };
        using var client = new HttpClient(handler);
        var transport = new HttpSyncTransport(client, RelayBase, NewSigner());

        await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await transport.SendAsync([0x01], ["bob-pub"]));
    }

    [Fact]
    public async Task TryReceiveAsync_EmptyInbox_ReturnsNull()
    {
        var handler = new StubHttpMessageHandler
        {
            Responder = _ => JsonOk("""{"cursor":0,"envelopes":[]}""")
        };
        using var client = new HttpClient(handler);
        var transport = new HttpSyncTransport(client, RelayBase, NewSigner());

        var result = await transport.TryReceiveAsync();

        Assert.Null(result);
        Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
    }

    [Fact]
    public async Task TryReceiveAsync_DrainsBatchedEnvelopesOneAtATime()
    {
        var handler = new StubHttpMessageHandler
        {
            Responder = _ => JsonOk($$"""
                {
                  "cursor": 7,
                  "envelopes": [
                    {"cursor": 6, "envelope": "{{Convert.ToBase64String([0x01])}}"},
                    {"cursor": 7, "envelope": "{{Convert.ToBase64String([0x02])}}"}
                  ]
                }
                """)
        };
        using var client = new HttpClient(handler);
        var transport = new HttpSyncTransport(client, RelayBase, NewSigner());

        var first = await transport.TryReceiveAsync();
        var second = await transport.TryReceiveAsync();

        Assert.Equal([0x01], first!);
        Assert.Equal([0x02], second!);
        // Two TryReceive calls should only have refilled once.
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task TryReceiveAsync_AdvancesCursorBetweenRefills()
    {
        int callCount = 0;
        var handler = new StubHttpMessageHandler
        {
            Responder = _ =>
            {
                callCount++;
                return callCount switch
                {
                    1 => JsonOk($$"""
                        {
                          "cursor": 5,
                          "envelopes": [
                            {"cursor": 5, "envelope": "{{Convert.ToBase64String([0xAA])}}"}
                          ]
                        }
                        """),
                    _ => JsonOk("""{"cursor":5,"envelopes":[]}""")
                };
            }
        };
        using var client = new HttpClient(handler);
        var transport = new HttpSyncTransport(client, RelayBase, NewSigner());

        // Drain the one envelope, then trigger a second refill which is empty.
        var got = await transport.TryReceiveAsync();
        var noMore = await transport.TryReceiveAsync();

        Assert.Equal([0xAA], got!);
        Assert.Null(noMore);
        Assert.Equal(2, handler.Requests.Count);

        var firstQuery = QueryOf(handler.Requests[0].RequestUri);
        var secondQuery = QueryOf(handler.Requests[1].RequestUri);
        Assert.Equal("0", firstQuery["since"]);
        Assert.Equal("5", secondQuery["since"]);
    }

    [Fact]
    public async Task TryReceiveAsync_SignsChallengeAndAttachesHeaders()
    {
        var signer = NewSigner();
        var handler = new StubHttpMessageHandler
        {
            Responder = _ => JsonOk("""{"cursor":0,"envelopes":[]}""")
        };
        using var client = new HttpClient(handler);
        var transport = new HttpSyncTransport(client, RelayBase, signer);

        await transport.TryReceiveAsync();

        var req = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, req.Method);

        var query = QueryOf(req.RequestUri);
        Assert.Equal(AlicePub, query["recipient"]);
        Assert.Equal("0", query["since"]);

        Assert.True(req.Headers.TryGetValue("X-Timestamp", out var ts));
        Assert.True(req.Headers.TryGetValue("X-Sig", out var sig));
        Assert.Equal(signer.SignatureToReturn, sig);
        Assert.True(long.TryParse(ts, out _));

        var signed = Assert.Single(signer.SignedMessages);
        Assert.Equal($"{ts}|{AlicePub}", signed);
    }

    [Fact]
    public async Task TryReceiveAsync_NonSuccessStatus_Throws()
    {
        var handler = new StubHttpMessageHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        };
        using var client = new HttpClient(handler);
        var transport = new HttpSyncTransport(client, RelayBase, NewSigner());

        await Assert.ThrowsAsync<HttpRequestException>(
            async () => await transport.TryReceiveAsync());
    }

    [Fact]
    public async Task TryReceiveAsync_PersistsCursorThroughStore()
    {
        var store = new InMemoryReceiveCursorStore();
        var handler = new StubHttpMessageHandler
        {
            Responder = _ => JsonOk($$"""
                {
                  "cursor": 42,
                  "envelopes": [
                    {"cursor": 42, "envelope": "{{Convert.ToBase64String([0xAA])}}"}
                  ]
                }
                """)
        };
        using var client = new HttpClient(handler);
        var transport = new HttpSyncTransport(client, RelayBase, NewSigner(), store);

        await transport.TryReceiveAsync(); // drains 0xAA, advances cursor

        Assert.Equal(42L, await store.LoadAsync());
    }

    [Fact]
    public async Task TryReceiveAsync_ResumesFromPersistedCursor()
    {
        var store = new InMemoryReceiveCursorStore();
        await store.SaveAsync(99);

        var handler = new StubHttpMessageHandler
        {
            Responder = _ => JsonOk("""{"cursor":99,"envelopes":[]}""")
        };
        using var client = new HttpClient(handler);
        var transport = new HttpSyncTransport(client, RelayBase, NewSigner(), store);

        await transport.TryReceiveAsync();

        var query = QueryOf(handler.Requests[0].RequestUri);
        Assert.Equal("99", query["since"]);
    }

    private static StubReceiveAuthSigner NewSigner() => new()
    {
        OwnEd25519PublicKeyBase64 = AlicePub,
    };

    private static HttpResponseMessage JsonOk(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static IReadOnlyDictionary<string, string> QueryOf(Uri uri)
    {
        var query = uri.Query.TrimStart('?');
        return query
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(
                p => Uri.UnescapeDataString(p[0]),
                p => p.Length == 2 ? Uri.UnescapeDataString(p[1]) : "",
                StringComparer.Ordinal);
    }
}
