using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TestGen.Analysis;

public sealed record TestConventions(
    string Framework,
    string Mocking,
    string Assertions,
    bool UsesAutoFixture,
    string NamingPattern,
    bool UsesAaaComments,
    string SetupStyle,
    string UsingsOrder,
    bool FileScopedNamespaces,
    IReadOnlyList<string> Usings,
    string? SampleTestMethod)
{
    public static TestConventions Default(string framework, string mocking, string assertions) =>
        new(framework, mocking, assertions, false, "Method_Scenario_Expected", true, "Constructor", "SystemFirst", true,
            new[] { NamespaceFor(framework), NamespaceFor(mocking), NamespaceFor(assertions) }.OfType<string>().Distinct().ToList(), null);

    internal static string? NamespaceFor(string library) => library.ToLowerInvariant() switch
    {
        "xunit" => "Xunit",
        "nunit" => "NUnit.Framework",
        "mstest" => "Microsoft.VisualStudio.TestTools.UnitTesting",
        "nsubstitute" => "NSubstitute",
        "moq" => "Moq",
        "fakeiteasy" => "FakeItEasy",
        "shouldly" => "Shouldly",
        "fluentassertions" => "FluentAssertions",
        _ => null,
    };

    public string Summarize()
    {
        var sb = new StringBuilder()
            .AppendLine($"Test framework: {Framework}")
            .AppendLine($"Mocking library: {Mocking}")
            .AppendLine($"Assertion library: {Assertions}")
            .AppendLine($"AutoFixture: {(UsesAutoFixture ? "yes" : "no")}")
            .AppendLine($"Test method naming: {NamingPattern}")
            .AppendLine($"AAA comments (// Arrange, // Act, // Assert): {(UsesAaaComments ? "yes" : "no")}")
            .AppendLine($"Setup style: {SetupStyle}")
            .AppendLine($"Usings order: {UsingsOrder}")
            .AppendLine($"Namespaces: {(FileScopedNamespaces ? "file-scoped" : "block")}");
        if (Usings.Count > 0) sb.AppendLine($"Common usings: {string.Join(", ", Usings)}");
        if (SampleTestMethod is not null) sb.AppendLine("Example test:").AppendLine(SampleTestMethod);
        return sb.ToString().TrimEnd();
    }
}

public static class ConventionDetector
{
    static readonly Regex ScenarioPattern = new(@"^[^_]+_[^_]+_[^_]+$", RegexOptions.Compiled);

    public static async Task<TestConventions> DetectAsync(Solution solution, CancellationToken ct)
    {
        var framework = new Dictionary<string, int>();
        var mocking = new Dictionary<string, int>();
        var assertions = new Dictionary<string, int>();
        var naming = new Dictionary<string, int>();
        var setup = new Dictionary<string, int>();
        var usingCounts = new Dictionary<string, int>();
        int autoFixture = 0, aaa = 0, testCount = 0, testFiles = 0, fileScoped = 0, blockNs = 0, systemFirst = 0, alphabetical = 0;
        var samples = new List<string>();
        var ownNamespaces = solution.Projects.Select(p => string.IsNullOrEmpty(p.DefaultNamespace) ? p.Name : p.DefaultNamespace).ToList();

        foreach (var doc in SolutionLoader.TestProjects(solution).SelectMany(p => p.Documents))
        {
            if (doc.FilePath?.Replace('\\', '/').Contains("/obj/") == true) continue;
            if (await doc.GetSyntaxRootAsync(ct) is not CompilationUnitSyntax root) continue;

            var usings = root.DescendantNodes().OfType<UsingDirectiveSyntax>()
                .Where(u => u.Alias is null && u.StaticKeyword == default && u.Name is not null)
                .Select(u => u.Name!.ToString()).ToList();
            foreach (var u in usings)
            {
                if (u.StartsWith("NSubstitute")) Bump(mocking, "NSubstitute");
                else if (u.StartsWith("Moq")) Bump(mocking, "Moq");
                else if (u.StartsWith("FakeItEasy")) Bump(mocking, "FakeItEasy");
                else if (u.StartsWith("FluentAssertions")) Bump(assertions, "FluentAssertions");
                else if (u.StartsWith("Shouldly")) Bump(assertions, "Shouldly");
                else if (u.StartsWith("AutoFixture")) autoFixture++;
                else if (u == "Xunit") Bump(framework, "xunit");
                else if (u.StartsWith("NUnit")) Bump(framework, "nunit");
                else if (u.StartsWith("Microsoft.VisualStudio.TestTools")) Bump(framework, "mstest");
            }

            var methods = TestSyntax.TestMethods(root).ToList();
            if (methods.Count == 0)
            {
                foreach (var cls in root.DescendantNodes().OfType<ClassDeclarationSyntax>().Where(c => c.Identifier.Text.EndsWith("Builder")))
                    Bump(setup, "Builders");
                continue;
            }

            testFiles++;
            foreach (var u in usings.Where(u => !ownNamespaces.Any(n => u == n || u.StartsWith(n + ".", StringComparison.Ordinal))).Distinct())
                Bump(usingCounts, u);
            if (root.Members.OfType<FileScopedNamespaceDeclarationSyntax>().Any()) fileScoped++;
            else if (root.Members.OfType<NamespaceDeclarationSyntax>().Any()) blockNs++;

            var topUsings = root.Usings.Where(u => u.GlobalKeyword == default && u.Alias is null && u.Name is not null).Select(u => u.Name!.ToString()).ToList();
            if (topUsings.Count >= 2)
            {
                var alpha = topUsings.SequenceEqual(topUsings.Order(StringComparer.Ordinal));
                var sys = topUsings.SequenceEqual(SortUsings(topUsings, "SystemFirst"));
                if (alpha && !sys) alphabetical++;
                else if (sys && !alpha) systemFirst++;
            }

            foreach (var m in methods)
            {
                testCount++;
                foreach (var a in TestSyntax.AttributeNames(m))
                {
                    if (a is "Fact" or "Theory") Bump(framework, "xunit");
                    else if (a is "Test" or "TestCase" or "TestCaseSource") Bump(framework, "nunit");
                    else if (a is "TestMethod" or "DataTestMethod") Bump(framework, "mstest");
                }
                Bump(naming, ClassifyName(m.Identifier.Text));
                var text = m.ToFullString();
                if (text.Contains("// Arrange", StringComparison.OrdinalIgnoreCase) || text.Contains("// Act", StringComparison.OrdinalIgnoreCase)) aaa++;
                samples.Add(m.ToString());
            }

            foreach (var cls in root.DescendantNodes().OfType<ClassDeclarationSyntax>().Where(c => c.Members.OfType<MethodDeclarationSyntax>().Any(TestSyntax.IsTestMethod)))
            {
                if (cls.Members.OfType<MethodDeclarationSyntax>().Any(x => TestSyntax.AttributeNames(x).Any(a => a is "SetUp" or "TestInitialize"))) Bump(setup, "SetUp");
                else if (cls.BaseList?.Types.Any(t => t.Type is GenericNameSyntax { Identifier.Text: "IClassFixture" or "ICollectionFixture" }) == true) Bump(setup, "ClassFixture");
                else if (cls.Members.OfType<ConstructorDeclarationSyntax>().Any()) Bump(setup, "Constructor");
            }
        }

        var fw = Top(framework) ?? "unknown";
        var conv = new TestConventions(
            fw,
            Top(mocking) ?? "none",
            Top(assertions) ?? (fw == "unknown" ? "xunit" : fw),
            autoFixture > 0,
            Top(naming) ?? "Unknown",
            aaa > 0 && aaa * 2 >= testCount,
            Top(setup) ?? "None",
            systemFirst > alphabetical ? "SystemFirst" : alphabetical > systemFirst ? "Alphabetical" : "Unknown",
            fileScoped >= blockNs && fileScoped > 0,
            [],
            samples.OrderBy(s => s.Length).FirstOrDefault(s => s.Length >= 120) ?? samples.MaxBy(s => s.Length));
        var common = usingCounts.Where(kv => kv.Value * 2 >= testFiles).Select(kv => kv.Key).ToList();
        return conv with { Usings = SortUsings(common, conv.UsingsOrder).ToList() };
    }

    internal static IEnumerable<string> SortUsings(IEnumerable<string> names, string order) =>
        order == "Alphabetical"
            ? names.Order(StringComparer.Ordinal)
            : names.OrderBy(n => n == "System" || n.StartsWith("System.", StringComparison.Ordinal) ? 0 : 1).ThenBy(n => n, StringComparer.Ordinal);

    static string ClassifyName(string n) =>
        n.StartsWith("Should", StringComparison.Ordinal) ? "Should_X"
        : n.Contains("_Should", StringComparison.Ordinal) ? "MethodName_Should_X"
        : ScenarioPattern.IsMatch(n) ? "Method_Scenario_Expected"
        : "Unknown";

    static void Bump(Dictionary<string, int> d, string key) => d[key] = d.GetValueOrDefault(key) + 1;

    static string? Top(Dictionary<string, int> d) => d.Count == 0 ? null : d.MaxBy(kv => kv.Value).Key;
}
