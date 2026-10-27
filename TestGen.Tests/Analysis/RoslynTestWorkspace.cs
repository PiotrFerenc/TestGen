using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace TestGen.Tests.Analysis;

/// <summary>Builds an in-memory solution (production project + test project referencing it) without MSBuild.</summary>
public static class RoslynTestWorkspace
{
    public const string Root = "/repo";

    static readonly MetadataReference[] Framework = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator)
        .Where(p => Path.GetFileName(p) is var f && (f.StartsWith("System.") || f is "System.dll" or "mscorlib.dll" or "netstandard.dll"))
        .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
        .ToArray();

    public static readonly MetadataReference[] TestLibraries =
        new[] { typeof(Xunit.FactAttribute), typeof(Xunit.Assert), typeof(Xunit.Abstractions.ITestOutputHelper), typeof(NSubstitute.Substitute), typeof(Shouldly.ShouldAssertException) }
            .Select(t => (MetadataReference)MetadataReference.CreateFromFile(t.Assembly.Location))
            .ToArray();

    public static string ProdDir(string name = "Acme.Core") => $"{Root}/src/{name}";
    public static string TestDir(string name = "Acme.Core.Tests") => $"{Root}/tests/{name}";

    public static Solution Create(
        (string Path, string Code)[] prod,
        (string Path, string Code)[]? tests = null,
        string prodName = "Acme.Core",
        string testName = "Acme.Core.Tests",
        bool withTestLibraries = true)
    {
        var solution = new AdhocWorkspace().CurrentSolution;
        var prodId = ProjectId.CreateNewId();
        solution = AddProject(solution, prodId, prodName, ProdDir(prodName), Framework, prod);
        if (tests is null) return solution;

        var testId = ProjectId.CreateNewId();
        solution = AddProject(solution, testId, testName, TestDir(testName), withTestLibraries ? [.. Framework, .. TestLibraries] : Framework, tests);
        return solution.AddProjectReference(testId, new ProjectReference(prodId));
    }

    static Solution AddProject(Solution solution, ProjectId id, string name, string dir, IEnumerable<MetadataReference> refs, (string Path, string Code)[] files)
    {
        var info = ProjectInfo.Create(id, VersionStamp.Default, name, name, LanguageNames.CSharp,
                filePath: $"{dir}/{name}.csproj",
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable),
                parseOptions: new CSharpParseOptions(LanguageVersion.Latest),
                metadataReferences: refs)
            .WithDefaultNamespace(name);
        solution = solution.AddProject(info);
        foreach (var (path, code) in files)
        {
            var folders = path.Split('/')[..^1];
            solution = solution.AddDocument(DocumentId.CreateNewId(id), Path.GetFileName(path), SourceText.From(code), folders, $"{dir}/{path}");
        }
        return solution;
    }

    public static Document Doc(Solution solution, string fileName) =>
        solution.Projects.SelectMany(p => p.Documents).Single(d => d.Name == fileName);

    public static Project Project(Solution solution, string name) => solution.Projects.Single(p => p.Name == name);
}
