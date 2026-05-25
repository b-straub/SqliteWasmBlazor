using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace SqliteWasmBlazor.Tests.Infrastructure;

internal sealed class StaticWebAssetTestServer : IAsyncDisposable
{
    private static readonly IReadOnlyDictionary<string, string> ContentTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".mjs"] = "text/javascript; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
        [".wasm"] = "application/wasm",
        [".dll"] = "application/octet-stream",
        [".dat"] = "application/octet-stream",
        [".pdb"] = "application/octet-stream",
        [".blat"] = "application/octet-stream",
        [".png"] = "image/png",
        [".ico"] = "image/x-icon",
        [".svg"] = "image/svg+xml",
        [".woff2"] = "font/woff2",
        [".ttf"] = "font/ttf"
    };

    private readonly int _port;
    private readonly string _pathBase;
    private readonly string[] _contentRoots;
    private readonly JsonElement _root;
    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _listener;
    private Task? _serveTask;

    public StaticWebAssetTestServer(int port, string pathBase = "")
    {
        _port = port;
        _pathBase = NormalizePathBase(pathBase);

        var manifestPath = Path.Combine(
            AppContext.BaseDirectory,
            "SqliteWasmBlazor.TestApp.staticwebassets.runtime.json");
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));

        _contentRoots = document.RootElement
            .GetProperty("ContentRoots")
            .EnumerateArray()
            .Select(root => root.GetString() ?? string.Empty)
            .ToArray();
        _root = document.RootElement.GetProperty("Root").Clone();
    }

    public void Start()
    {
        if (_listener is not null)
        {
            return;
        }

        _listener = new TcpListener(IPAddress.Loopback, _port);
        _listener.Start();
        _serveTask = Task.Run(() => ServeAsync(_cts.Token));
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener?.Stop();

        if (_serveTask is not null)
        {
            try
            {
                await _serveTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        _cts.Dispose();
    }

    private async Task ServeAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var client = await _listener!.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            _ = Task.Run(() => HandleClientAsync(client, cancellationToken), cancellationToken);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var _ = client;
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);

        var requestLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(requestLine))
        {
            return;
        }

        string? line;
        do
        {
            line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        }
        while (!string.IsNullOrEmpty(line));

        var parts = requestLine.Split(' ', 3);
        if (parts.Length < 2)
        {
            await WriteStatusAsync(stream, 400, "Bad Request", cancellationToken).ConfigureAwait(false);
            return;
        }

        var method = parts[0];
        var requestPath = Uri.UnescapeDataString(parts[1].Split('?', 2)[0]);
        if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase))
        {
            await WriteStatusAsync(stream, 405, "Method Not Allowed", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!TryStripPathBase(requestPath, out var assetPath))
        {
            await WriteStatusAsync(stream, 404, "Not Found", cancellationToken).ConfigureAwait(false);
            return;
        }

        var resolved = ResolveAsset(assetPath);
        if (resolved is null)
        {
            await WriteStatusAsync(stream, 404, "Not Found", cancellationToken).ConfigureAwait(false);
            return;
        }

        var bytes = await File.ReadAllBytesAsync(resolved.Value.Path, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(_pathBase) && resolved.Value.IsIndexHtml)
        {
            var html = Encoding.UTF8.GetString(bytes)
                .Replace("<base href=\"/\" />", $"<base href=\"{_pathBase}/\" />", StringComparison.Ordinal);
            bytes = Encoding.UTF8.GetBytes(html);
        }

        var contentType = ContentTypes.TryGetValue(Path.GetExtension(resolved.Value.Path), out var knownType)
            ? knownType
            : "application/octet-stream";
        await WriteHeadersAsync(stream, 200, "OK", contentType, bytes.Length, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase))
        {
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
    }

    private (string Path, bool IsIndexHtml)? ResolveAsset(string requestPath)
    {
        if (requestPath == "/")
        {
            requestPath = "/index.html";
        }

        var manifestAsset = ResolveManifestAsset(requestPath);
        if (manifestAsset is not null)
        {
            return (manifestAsset, requestPath.Equals("/index.html", StringComparison.OrdinalIgnoreCase));
        }

        var index = ResolveManifestAsset("/index.html");
        return index is null ? null : (index, true);
    }

    private string? ResolveManifestAsset(string requestPath)
    {
        var node = _root;
        foreach (var part in requestPath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!node.TryGetProperty("Children", out var children) ||
                children.ValueKind != JsonValueKind.Object ||
                !children.TryGetProperty(part, out node))
            {
                return null;
            }
        }

        if (!node.TryGetProperty("Asset", out var asset) ||
            asset.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var contentRootIndex = asset.GetProperty("ContentRootIndex").GetInt32();
        var subPath = asset.GetProperty("SubPath").GetString();
        if (string.IsNullOrWhiteSpace(subPath) || contentRootIndex < 0 || contentRootIndex >= _contentRoots.Length)
        {
            return null;
        }

        var fullPath = Path.GetFullPath(Path.Combine(_contentRoots[contentRootIndex], subPath));
        var contentRoot = Path.GetFullPath(_contentRoots[contentRootIndex]);
        return fullPath.StartsWith(contentRoot, StringComparison.Ordinal) && File.Exists(fullPath)
            ? fullPath
            : null;
    }

    private bool TryStripPathBase(string requestPath, out string assetPath)
    {
        if (string.IsNullOrEmpty(_pathBase))
        {
            assetPath = requestPath;
            return true;
        }

        if (!requestPath.StartsWith(_pathBase, StringComparison.OrdinalIgnoreCase))
        {
            assetPath = "/";
            return false;
        }

        assetPath = requestPath[_pathBase.Length..];
        if (string.IsNullOrEmpty(assetPath))
        {
            assetPath = "/";
        }

        return true;
    }

    private static async Task WriteStatusAsync(
        Stream stream,
        int statusCode,
        string reason,
        CancellationToken cancellationToken)
    {
        await WriteHeadersAsync(stream, statusCode, reason, "text/plain; charset=utf-8", 0, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteHeadersAsync(
        Stream stream,
        int statusCode,
        string reason,
        string contentType,
        int contentLength,
        CancellationToken cancellationToken)
    {
        var headers =
            $"HTTP/1.1 {statusCode} {reason}\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {contentLength}\r\n" +
            "Cache-Control: no-store\r\n" +
            "Cross-Origin-Opener-Policy: same-origin\r\n" +
            "Cross-Origin-Embedder-Policy: require-corp\r\n" +
            "Cross-Origin-Resource-Policy: cross-origin\r\n" +
            "Connection: close\r\n\r\n";
        var bytes = Encoding.ASCII.GetBytes(headers);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    private static string NormalizePathBase(string pathBase)
    {
        if (string.IsNullOrWhiteSpace(pathBase) || pathBase == "/")
        {
            return string.Empty;
        }

        return pathBase.StartsWith('/') ? pathBase.TrimEnd('/') : "/" + pathBase.TrimEnd('/');
    }
}
