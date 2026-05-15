using Xunit;

namespace SqliteWasmBlazor.CryptoSync.Tests;

/// <summary>
/// Bundle-composition regression guard for the Phase 4 worker-bundle split.
///
/// <para>
/// Plane 1 ships a crypto-free worker bundle from
/// <c>src/Base/SqliteWasmBlazor/wwwroot/sqlite-wasm-worker.js</c>. Plane 2
/// ships a separate, crypto-equipped bundle from
/// <c>src/Crypto/SqliteWasmBlazor.Crypto/wwwroot/sqlite-wasm-worker.js</c>.
/// The split keeps plane-1-only consumers (no <c>AddSqliteWasmBlazorCrypto()</c>
/// call) off the encrypted-VFS payload.
/// </para>
///
/// <para>
/// The invariant we lock here: <b>any token that proves "crypto is wired
/// into this bundle"</b> must be present in plane 2 and absent from plane 1.
/// Sentinel tokens we probe: <c>@awasm/noble</c>, <c>chacha20</c>, <c>poly1305</c>,
/// <c>vfs-prf</c>, <c>sahpool-prf</c>. The check is purely substring-based —
/// it doesn't need to parse the bundle, it just verifies the esbuild
/// output's source/identifier surface.
/// </para>
///
/// <para>
/// A failure means either: (a) plane-1 worker accidentally imports a
/// crypto module (regression on the Phase 4 split), or (b) plane-2 worker
/// lost a crypto module reference (broken bundle config). Either way the
/// diagnostic points at the specific token.
/// </para>
/// </summary>
public class WorkerBundleCompositionTests
{
    private const string Plane1WorkerRelPath = "src/Base/SqliteWasmBlazor/wwwroot/sqlite-wasm-worker.js";
    private const string Plane2WorkerRelPath = "src/Crypto/SqliteWasmBlazor.Crypto/wwwroot/sqlite-wasm-worker.js";

    // Token signatures that indicate crypto is wired into the bundle.
    // Each one targets a different layer of the crypto stack:
    //  - @awasm/noble — the WASM noble package (chacha20poly1305 + sha256/hkdf/hmac).
    //  - chacha20    — the page-crypt AEAD primitive name.
    //  - poly1305    — the MAC component of the same AEAD.
    //  - vfs-prf     — the VFS module folder name (PRF-keyed page crypt).
    //  - sahpool-prf — the VFS file path embedded by esbuild (sahpool-prf-vfs.ts).
    //                  installPrfVfs itself is inlined as an alias import and the
    //                  identifier doesn't survive bundling; sahpool-prf does.
    private static readonly string[] CryptoTokens =
    [
        "@awasm/noble",
        "chacha20",
        "poly1305",
        "vfs-prf",
        "sahpool-prf",
    ];

    [Fact]
    public void Plane1WorkerBundle_ContainsNoCryptoTokens()
    {
        var bundle = ReadBundle(Plane1WorkerRelPath);
        foreach (var token in CryptoTokens)
        {
            Assert.False(
                bundle.Contains(token, StringComparison.Ordinal),
                $"Plane 1 worker bundle leaked crypto token '{token}'. " +
                $"Phase 4 split invariant broken — a crypto module is being pulled into " +
                $"the plain-engine bundle. Path: {Plane1WorkerRelPath}");
        }
    }

    [Fact]
    public void Plane2WorkerBundle_ContainsAllCryptoTokens()
    {
        var bundle = ReadBundle(Plane2WorkerRelPath);
        foreach (var token in CryptoTokens)
        {
            Assert.True(
                bundle.Contains(token, StringComparison.Ordinal),
                $"Plane 2 worker bundle missing crypto token '{token}'. " +
                $"Either the bundle config dropped a crypto import or the token " +
                $"shape changed. Path: {Plane2WorkerRelPath}");
        }
    }

    [Fact]
    public void Plane1Wwwroot_HasNoPrfAssets()
    {
        // crypto-bridge.js is plane-2-only (the PRF WebAuthn pipeline loaded
        // by PrfService + CryptoInterop via SqliteWasmBlazorCryptoOptions.AssetRoot).
        // It must not appear in plane 1's wwwroot — plane-split invariant:
        // a plane never ships plane N+1 assets.
        var repoRoot = FindRepoRoot();
        var leak = Path.Combine(repoRoot, "src/Base/SqliteWasmBlazor/wwwroot/crypto-bridge.js");
        Assert.False(File.Exists(leak),
            $"Plane 1 wwwroot leaked plane-2 asset crypto-bridge.js at {leak}. " +
            $"Move it to src/Crypto/SqliteWasmBlazor.Crypto/wwwroot/ — plane-split " +
            $"invariant: a plane never ships plane N+1 assets.");
    }

    private static string ReadBundle(string repoRelPath)
    {
        var repoRoot = FindRepoRoot();
        var fullPath = Path.Combine(repoRoot, repoRelPath);
        Assert.True(File.Exists(fullPath),
            $"Bundle not found at {fullPath}. The TypeScript build must run before this test; " +
            $"`dotnet build` triggers esbuild via the per-csproj BuildTypeScript* targets.");
        return File.ReadAllText(fullPath);
    }

    private static string FindRepoRoot()
    {
        // Walk up from the test assembly's bin folder until we find SqliteWasmBlazor.slnx.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SqliteWasmBlazor.slnx")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
