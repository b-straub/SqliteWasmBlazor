using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.DependencyModel;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using SqliteWasmBlazor.CryptoSync.Generator;

namespace GeneratorTest.Helpers
{
    internal class TestAnalyzerConfigOptions(IEnumerable<(string key, string value)> options) : AnalyzerConfigOptions
    {
        private readonly Dictionary<string, string> _options = options.ToDictionary(e => e.key, e => e.value);

        public override bool TryGetValue(string key, [NotNullWhen(true)] out string? value)
            => _options.TryGetValue(key, out value);
    }

    internal class TestAnalyzerConfigOptionsProvider(IEnumerable<(string key, string value)> options) : AnalyzerConfigOptionsProvider
    {
        public override AnalyzerConfigOptions GlobalOptions { get; } = new TestAnalyzerConfigOptions(options);

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree)
            => GlobalOptions;

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile)
            => GlobalOptions;
    }

    static class GeneratorFactory
    {
        public static ImmutableArray<Diagnostic> RunGenerator(IEnumerable<string> sources, IEnumerable<(string Text, string Path)> additionalTexts)
        {
            List<SyntaxTree> syntaxTrees = [];

            foreach (var source in sources)
            {
                var syntaxTree = CSharpSyntaxTree.ParseText(SourceText.From(source, Encoding.UTF8), CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview));
                syntaxTrees.Add(syntaxTree);
            }

            var compilationOptions = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithNullableContextOptions(NullableContextOptions.Enable)
                .WithOptimizationLevel(OptimizationLevel.Debug)
                .WithGeneralDiagnosticOption(ReportDiagnostic.Default);

            var allReferences = DependencyContext.Default is null ? new List<PortableExecutableReference>() :
                DependencyContext.Default.CompileLibraries
                    .SelectMany(cl => cl.ResolveReferencePaths())
                    .Select(asm => MetadataReference.CreateFromFile(asm)).ToList();

            var metaDataReferences = allReferences;

            Compilation compilation = CSharpCompilation.Create("testgenerator", syntaxTrees, metaDataReferences, compilationOptions);
            var parseOptions = syntaxTrees.FirstOrDefault()?.Options as CSharpParseOptions;
            CryptoSyncGenerator? generator = new();
            
            GeneratorDriver driver = CSharpGeneratorDriver.
                Create([generator.AsSourceGenerator()], parseOptions: parseOptions);

            driver.RunGeneratorsAndUpdateCompilation(compilation, out var generatorCompilation, out var generatorDiagnostics);

            // Print and save generated files for debugging
            Console.WriteLine("\n=== Generated Files ===");
            var outputDir = Path.Combine(GetProjectRootFromBase(), "GeneratedOutput");
            Directory.CreateDirectory(outputDir);

            foreach (var syntaxTree in generatorCompilation.SyntaxTrees)
            {
                if (syntaxTree.FilePath.Contains(".g.cs"))
                {
                    var fileName = Path.GetFileName(syntaxTree.FilePath);
                    var content = syntaxTree.GetText().ToString();

                    Console.WriteLine($"\nFile: {fileName}");
                    Console.WriteLine(content);
                    Console.WriteLine("\n" + new string('=', 50));

                    // Save to disk
                    var outputPath = Path.Combine(outputDir, fileName);
                    File.WriteAllText(outputPath, content);
                    Console.WriteLine($"Saved to: {outputPath}");
                }
            }

            return generatorDiagnostics;
        }

        private class RazorAdditionalText(string text, string path) : AdditionalText
        {
            public override string Path { get; } = path;

            public override SourceText GetText(CancellationToken cancellationToken = new())
            {
                return SourceText.From(text);
            }
        }

        private static string GetProjectRootFromBase()
        {
            var baseDirectory = AppContext.BaseDirectory;
            var current = new DirectoryInfo(baseDirectory);

            // Navigate up to find the project root (where .csproj file is)
            while (current != null && !current.GetFiles("*.csproj").Any())
            {
                current = current.Parent;
            }

            return current?.FullName ?? baseDirectory;
        }
    }
}
