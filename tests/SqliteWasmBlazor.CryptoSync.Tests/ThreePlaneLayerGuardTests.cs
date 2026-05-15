using System.Reflection;
using SqliteWasmBlazor.Crypto.Services;
using SqliteWasmBlazor.Crypto.UI.Components.Authentication;
using Xunit;

namespace SqliteWasmBlazor.CryptoSync.Tests;

/// <summary>
/// Build-time regression guards for the three-plane architecture:
/// <list type="bullet">
///   <item>Plane 1 — plain SQLite engine: <c>SqliteWasmBlazor</c>.</item>
///   <item>Plane 2 — PRF-keyed at-rest encryption: <c>SqliteWasmBlazor.Crypto</c>
///         (engine) + <c>SqliteWasmBlazor.Crypto.UI</c> (panels).</item>
///   <item>Plane 3 — encrypted multi-device sync: <c>SqliteWasmBlazor.CryptoSync</c>
///         (engine) + <c>SqliteWasmBlazor.CryptoSync.UI</c> (panels).</item>
/// </list>
/// Invariants:
/// <list type="bullet">
///   <item>A plane-N library must never reference a plane-N+1 assembly.</item>
///   <item>Within a plane, the engine never references its own UI sub-assembly.</item>
///   <item>Plane 1's public surface contains zero types whose namespace begins with
///         <c>SqliteWasmBlazor.Crypto</c> — the engine was carved out in plane-split Phase 2b.</item>
/// </list>
/// These tests inspect <see cref="Assembly.GetReferencedAssemblies"/> + exported types at
/// runtime so a stray upward reference (or accidental public type promotion) surfaces
/// with a clear diagnostic rather than a transitive type-load error.
/// </summary>
public class ThreePlaneLayerGuardTests
{
    private static IReadOnlyList<string> ReferencedAssemblyNames<TInPlane>() =>
        typeof(TInPlane).Assembly
            .GetReferencedAssemblies()
            .Select(r => r.Name ?? string.Empty)
            .ToList();

    private static bool IsCryptoSyncAssembly(string name) =>
        name.StartsWith("SqliteWasmBlazor.CryptoSync", StringComparison.Ordinal);

    private static bool IsCryptoAssembly(string name) =>
        name.StartsWith("SqliteWasmBlazor.Crypto", StringComparison.Ordinal)
        && !IsCryptoSyncAssembly(name);

    private static bool IsCryptoUiAssembly(string name) =>
        name.Equals("SqliteWasmBlazor.Crypto.UI", StringComparison.Ordinal);

    [Fact]
    public void Plane1Engine_DoesNotReference_HigherPlanes()
    {
        var leaks = ReferencedAssemblyNames<SqliteWasmOptions>()
            .Where(name => IsCryptoAssembly(name) || IsCryptoSyncAssembly(name))
            .ToList();

        Assert.True(
            leaks.Count == 0,
            $"Plane 1 (plain SQLite engine) SqliteWasmBlazor must not reference Crypto / Crypto.UI / CryptoSync.* " +
            $"— found: [{string.Join(", ", leaks)}]");
    }

    [Fact]
    public void Plane1Engine_PublicSurface_HasNoCryptoTypes()
    {
        // After plane-split Phase 2b's carve-out, no exported type in
        // SqliteWasmBlazor.dll may live in the SqliteWasmBlazor.Crypto.* or
        // SqliteWasmBlazor.CryptoSync.* namespaces. Catches an accidental
        // type-relocation regression that the reference-direction guard above
        // would miss (an internal type in plane 1 doesn't trigger a ref).
        var leaks = typeof(SqliteWasmOptions).Assembly
            .GetExportedTypes()
            .Where(t => t.Namespace is not null
                && (t.Namespace.StartsWith("SqliteWasmBlazor.Crypto", StringComparison.Ordinal)
                    || t.Namespace.StartsWith("SqliteWasmBlazor.CryptoSync", StringComparison.Ordinal)))
            .Select(t => $"{t.Namespace}.{t.Name}")
            .ToList();

        Assert.True(
            leaks.Count == 0,
            $"Plane 1 (plain SQLite engine) SqliteWasmBlazor.dll exports types in plane-2/3 " +
            $"namespaces — found: [{string.Join(", ", leaks)}]");
    }

    [Fact]
    public void Plane2Engine_DoesNotReference_OwnUI()
    {
        // Within Plane 2, the engine assembly stays UI-agnostic so non-Blazor
        // hosts can consume the encrypted VFS without a UI dependency. The
        // UI sub-assembly sits on top of the engine, never below.
        var leaks = ReferencedAssemblyNames<EncryptedSqliteWasmWorkerBridge>()
            .Where(IsCryptoUiAssembly)
            .ToList();

        Assert.True(
            leaks.Count == 0,
            $"Plane 2 engine (SqliteWasmBlazor.Crypto) must not reference its UI sub-assembly " +
            $"SqliteWasmBlazor.Crypto.UI — found: [{string.Join(", ", leaks)}]");
    }

    [Fact]
    public void Plane2Engine_DoesNotReference_Plane3()
    {
        var leaks = ReferencedAssemblyNames<EncryptedSqliteWasmWorkerBridge>()
            .Where(IsCryptoSyncAssembly)
            .ToList();

        Assert.True(
            leaks.Count == 0,
            $"Plane 2 engine (SqliteWasmBlazor.Crypto) must not reference " +
            $"Plane 3 (CryptoSync.*) — found: [{string.Join(", ", leaks)}]");
    }

    [Fact]
    public void Plane2UI_DoesNotReference_Plane3()
    {
        var leaks = ReferencedAssemblyNames<AuthenticationModel>()
            .Where(IsCryptoSyncAssembly)
            .ToList();

        Assert.True(
            leaks.Count == 0,
            $"Plane 2 UI (SqliteWasmBlazor.Crypto.UI) must not reference " +
            $"Plane 3 (CryptoSync.*) — found: [{string.Join(", ", leaks)}]");
    }

    [Fact]
    public void Plane3Engine_DoesNotReference_OwnUI()
    {
        // Within Plane 3, the engine assembly stays UI-agnostic so non-Blazor
        // hosts can consume the sync stack without a Blazor dependency.
        var leaks = ReferencedAssemblyNames<CryptoSyncContextBase>()
            .Where(name => name.Equals(
                "SqliteWasmBlazor.CryptoSync.UI", StringComparison.Ordinal))
            .ToList();

        Assert.True(
            leaks.Count == 0,
            $"Plane 3 engine (SqliteWasmBlazor.CryptoSync) must not reference its UI sub-assembly " +
            $"SqliteWasmBlazor.CryptoSync.UI — found: [{string.Join(", ", leaks)}]");
    }

    [Fact]
    public void NoLibrary_References_DemoSample()
    {
        // Samples consume libraries, never the other way round. A library
        // accidentally importing demo code (e.g. `using SqliteWasmBlazor.Demo`)
        // would force a sample → library dependency cycle.
        foreach (var (planeName, sample) in new (string, IReadOnlyList<string>)[]
        {
            ("Plane 1 SqliteWasmBlazor", ReferencedAssemblyNames<SqliteWasmOptions>()),
            ("Plane 2 engine SqliteWasmBlazor.Crypto", ReferencedAssemblyNames<EncryptedSqliteWasmWorkerBridge>()),
            ("Plane 2 UI SqliteWasmBlazor.Crypto.UI", ReferencedAssemblyNames<AuthenticationModel>()),
            ("Plane 3 engine SqliteWasmBlazor.CryptoSync", ReferencedAssemblyNames<CryptoSyncContextBase>()),
        })
        {
            var leaks = sample.Where(name =>
                name.StartsWith("SqliteWasmBlazor.Demo", StringComparison.Ordinal)
                || name.Equals("SqliteWasmBlazor.TestApp", StringComparison.Ordinal)
                || name.StartsWith("SqliteWasmBlazor.AdoNetSample", StringComparison.Ordinal)
                || name.StartsWith("SqliteWasmBlazor.FloatingWindow", StringComparison.Ordinal))
                .ToList();

            Assert.True(
                leaks.Count == 0,
                $"{planeName} must not reference any sample/demo project — found: [{string.Join(", ", leaks)}]");
        }
    }
}
