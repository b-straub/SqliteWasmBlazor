using GeneratorTest.Helpers;

namespace GeneratorRunner
{
    class Program
    {
        private static readonly string[] SourcePaths =
        [
            "../../SqliteWasmBlazor/SqliteWasmBlazor.CryptoSync.Tests/ContextTests.cs"
        ];

        private static readonly string[] AdditionalTextPaths =
        [
        ];

        public static void Main(string[] _)
        {
            List<string> sources = [];
            List<(string Text, string Path)> additionalText = [];

            foreach (var path in SourcePaths)
            {
                var fullPath = Path.Combine(GetProjectRootFromBase(), path);

                if (File.Exists(fullPath))
                {
                    var content = File.ReadAllText(fullPath);
                    if (content.IndexOf("using System;", StringComparison.InvariantCulture) == -1)
                    {
                        // fix implict usings;
                        content = "using System;" + Environment.NewLine + content;
                    }

                    sources.Add(content);
                    Console.WriteLine($"Loaded source: {fullPath}");
                }
                else
                {
                    throw new InvalidOperationException($"Error: File not found: {fullPath}");
                }
            }

            foreach (var path in AdditionalTextPaths)
            {
                var fullPath = Path.Combine(GetProjectRootFromBase(), path);
                if (File.Exists(fullPath))
                {
                    additionalText.Add((File.ReadAllText(fullPath), fullPath));
                    Console.WriteLine($"Loaded additional text: {fullPath}");
                }
                else
                {
                    throw new InvalidOperationException($"Error: Additional text file not found: {fullPath}");
                }
            }

            var diags = GeneratorFactory.RunGenerator(sources, additionalText);

            foreach (var diag in diags)
            {
                Console.WriteLine($"Message: {diag.GetMessage()}, Location: {diag.Location.GetLineSpan()}");
            }

            var outputDir = Path.Combine(GetProjectRootFromBase(), "GeneratedOutput");

            foreach (var path in SourcePaths)
            {
                File.Copy(Path.Combine(GetProjectRootFromBase(), path), Path.Combine(outputDir, Path.GetFileName(path)));
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
