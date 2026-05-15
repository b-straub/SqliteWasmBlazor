using System.Net;

namespace SqliteWasmBlazor.CryptoSync.Tests.Fixtures;

/// <summary>
/// In-process <see cref="HttpMessageHandler"/> for <see cref="HttpSyncTransport"/>
/// unit tests. Records every observed request (and its body, eagerly read so
/// downstream assertions don't race the message disposal) and dispatches to a
/// caller-supplied <see cref="Responder"/> for the canned response.
///
/// <para>
/// Kept deliberately small — it's a stub, not a recreation of the
/// "RecordingHttpHandler"-style fixture from the reverted Stage 3a attempt.
/// </para>
/// </summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    public List<RecordedRequest> Requests { get; } = [];

    public Func<RecordedRequest, HttpResponseMessage> Responder { get; set; }
        = _ => new HttpResponseMessage(HttpStatusCode.OK);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);

        var headers = request.Headers
            .ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);

        var recorded = new RecordedRequest(
            request.Method,
            request.RequestUri ?? throw new InvalidOperationException("request URI must be non-null"),
            headers,
            body);
        Requests.Add(recorded);

        return Responder(recorded);
    }

    public sealed record RecordedRequest(
        HttpMethod Method,
        Uri RequestUri,
        IReadOnlyDictionary<string, string> Headers,
        string? Body);
}
