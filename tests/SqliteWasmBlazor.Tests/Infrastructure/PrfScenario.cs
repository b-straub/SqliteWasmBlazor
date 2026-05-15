using System.Text.Json;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace SqliteWasmBlazor.Tests.Infrastructure;

/// <summary>
/// Per-test scope wrapping an isolated <see cref="IBrowserContext"/>, the
/// page under test, a CDP session, and one or more virtual authenticators.
/// Disposing tears the context down so each test gets a fresh OPFS profile.
/// </summary>
public sealed class PrfScenario : IAsyncDisposable
{
    private readonly IBrowserContext _context;
    private readonly ICDPSession _cdp;
    private readonly List<string> _authenticatorIds;

    public IPage Page { get; }
    public string PrimaryAuthenticatorId { get; }
    public int Port { get; }

    private PrfScenario(
        IBrowserContext context,
        IPage page,
        ICDPSession cdp,
        string primaryAuthId,
        int port)
    {
        _context = context;
        Page = page;
        _cdp = cdp;
        PrimaryAuthenticatorId = primaryAuthId;
        _authenticatorIds = [primaryAuthId];
        Port = port;
    }

    public static async Task<PrfScenario> CreateAsync(
        IBrowser browser,
        int port,
        ITestOutputHelper? output = null)
    {
        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = false,
        });

        context.WebError += (_, e) =>
        {
            var message = $"[Browser WebError] {e.Error}";
            output?.WriteLine(message);
            Console.Error.WriteLine(message);
        };

        var page = await context.NewPageAsync();
        page.Console += (_, msg) =>
        {
            var line = $"[Browser {msg.Type}] {msg.Text}";
            output?.WriteLine(line);
            if (msg.Type == "error")
            {
                Console.Error.WriteLine(line);
            }
        };

        var cdp = await context.NewCDPSessionAsync(page);
        await cdp.SendAsync("WebAuthn.enable");

        // Primary authenticator is the platform-style passkey (transport=internal)
        // — that's the production target for PrfVfsTest's flow.
        var authId = await AddVirtualAuthenticatorCoreAsync(cdp, transport: "internal");
        return new PrfScenario(context, page, cdp, authId, port);
    }

    /// <summary>
    /// Add a secondary virtual authenticator (e.g. for credential-mismatch
    /// scenarios where DB is locked under credential A and we attempt to
    /// unlock under credential B). Defaults to <c>transport: "usb"</c> because
    /// Chromium permits only one internal authenticator per CDP session;
    /// USB transport simulates a hardware key and equally supports PRF.
    /// </summary>
    public async Task<string> AddVirtualAuthenticatorAsync(string transport = "usb")
    {
        var id = await AddVirtualAuthenticatorCoreAsync(_cdp, transport);
        _authenticatorIds.Add(id);
        return id;
    }

    /// <summary>
    /// Disable a specific authenticator without removing it. Use when the
    /// test wants only authenticator B to answer the next ceremony.
    /// </summary>
    public async Task SetAuthenticatorEnabledAsync(string authenticatorId, bool enabled)
    {
        await _cdp.SendAsync("WebAuthn.setAutomaticPresenceSimulation", new Dictionary<string, object>
        {
            ["authenticatorId"] = authenticatorId,
            ["enabled"] = enabled,
        });
    }

    public async Task RemoveAuthenticatorAsync(string authenticatorId)
    {
        await _cdp.SendAsync("WebAuthn.removeVirtualAuthenticator", new Dictionary<string, object>
        {
            ["authenticatorId"] = authenticatorId,
        });
        _authenticatorIds.Remove(authenticatorId);
    }

    public async Task NavigateAsync(string path, int timeoutMs = 60000)
    {
        var url = path.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? path
            : $"http://localhost:{Port}{path}";
        await Page.GotoAsync(url, new PageGotoOptions { Timeout = timeoutMs });
    }

    /// <summary>
    /// Send a raw CDP command bypassing the typed helpers. Tests use this to
    /// query authenticator state (e.g. <c>WebAuthn.getCredentials</c>).
    /// </summary>
    public ValueTask<JsonElement?> SendCdpAsync(string method, Dictionary<string, object>? args = null)
        => new(_cdp.SendAsync(method, args));

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _cdp.DetachAsync();
        }
        catch
        {
            // CDP may already be torn down with the context — ignore.
        }

        await _context.DisposeAsync();
    }

    /// <summary>
    /// Sole place that pins the <c>WebAuthn.addVirtualAuthenticator</c>
    /// option shape. If Chromium ever renames a flag, this is the only line
    /// that needs an update.
    /// </summary>
    private static async Task<string> AddVirtualAuthenticatorCoreAsync(ICDPSession cdp, string transport)
    {
        var response = await cdp.SendAsync("WebAuthn.addVirtualAuthenticator", new Dictionary<string, object>
        {
            ["options"] = new Dictionary<string, object>
            {
                ["protocol"] = "ctap2",
                ["transport"] = transport,
                ["hasResidentKey"] = true,
                ["hasUserVerification"] = true,
                ["isUserVerified"] = true,
                ["hasPrf"] = true,
            },
        });

        if (response is null || !response.Value.TryGetProperty("authenticatorId", out var idElem))
        {
            throw new InvalidOperationException(
                "WebAuthn.addVirtualAuthenticator returned no authenticatorId.");
        }

        return idElem.GetString() ?? throw new InvalidOperationException(
            "WebAuthn.addVirtualAuthenticator returned a null authenticatorId.");
    }
}
