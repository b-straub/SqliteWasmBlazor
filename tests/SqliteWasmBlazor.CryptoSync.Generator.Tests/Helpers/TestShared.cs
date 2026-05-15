using Microsoft.CodeAnalysis.Testing;

namespace SqliteWasmBlazor.CryptoSync.Generator.Tests.Helpers;

internal static class TestShared
{
    public static ReferenceAssemblies ReferenceAssemblies()
    {
        var net10 = new ReferenceAssemblies(
            "net10.0",
            new PackageIdentity(
                "Microsoft.NETCore.App.Ref",
                "10.0.0"),
            Path.Combine("ref", "net10.0"));

        return net10;
    }
}
