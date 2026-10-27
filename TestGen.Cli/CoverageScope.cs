using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TestGen.Analysis;
using TestGen.Coverage;

namespace TestGen.Cli;

/// <summary>Aligns a cobertura snapshot with the solution: test-project classes are dropped and production projects
/// whose assembly never loaded during the test run (no tests reference it) get zero-hit entries built from Roslyn.</summary>
public static class CoverageScope
{
    public static CoverageSnapshot Apply(CoverageSnapshot snapshot, Solution solution)
    {
        // By directory, not document list: files added after the solution was loaded (merged tests) must be dropped too.
        var testDirs = SolutionLoader.TestProjects(solution).Select(p => Full(Path.GetDirectoryName(p.FilePath))).OfType<string>()
            .Select(d => d.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar).ToList();
        var reported = snapshot.Classes.Select(c => Full(c.SourceFile)).OfType<string>().ToHashSet();
        var classes = snapshot.Classes.Where(c => Full(c.SourceFile) is not { } f || !testDirs.Any(d => f.StartsWith(d, StringComparison.Ordinal))).ToList();

        // ponytail: statement start lines approximate cobertura sequence points; replaced by real numbers once tests load the assembly.
        var missing = SolutionLoader.ProductionProjects(solution)
            .Where(p => !p.Documents.Any(d => reported.Contains(Full(d.FilePath) ?? "")))
            .SelectMany(p => p.Documents)
            .SelectMany(Synthesize);
        var known = classes.Select(c => c.FullName).ToHashSet();
        classes.AddRange(new CoverageExclusions().Apply(snapshot with { Classes = missing.Where(c => known.Add(c.FullName)).ToList() }).Classes);
        return snapshot with { Classes = classes };
    }

    static IEnumerable<ClassCoverage> Synthesize(Document doc)
    {
        var root = doc.GetSyntaxRootAsync().GetAwaiter().GetResult();
        if (root is null) yield break;
        var tree = root.SyntaxTree;
        int Line(SyntaxNode n) => tree.GetLineSpan(n.Span).StartLinePosition.Line + 1;

        foreach (var type in root.DescendantNodes().OfType<TypeDeclarationSyntax>().Where(t => t is not InterfaceDeclarationSyntax))
        {
            var methods = type.Members.Select(m => m switch
                {
                    BaseMethodDeclarationSyntax b => (Name: b is ConstructorDeclarationSyntax ? ".ctor" : (b as MethodDeclarationSyntax)?.Identifier.Text ?? b.Kind().ToString(),
                        Body: (SyntaxNode?)b.Body ?? b.ExpressionBody),
                    PropertyDeclarationSyntax { ExpressionBody: { } e } p => ("get_" + p.Identifier.Text, e),
                    _ => (Name: "", Body: null),
                })
                .Where(m => m.Body is not null)
                .Select(m => new MethodCoverage(m.Name, "", (m.Body is ArrowExpressionClauseSyntax ? [m.Body] : m.Body!.DescendantNodes().OfType<StatementSyntax>().Where(s => s is not BlockSyntax).ToArray<SyntaxNode>())
                    .Select(Line).Distinct().Select(l => new LineCoverage(l, 0, false, 0, 0)).ToList()))
                .Where(m => m.Lines.Count > 0)
                .ToList();
            if (methods.Count == 0) continue;

            var names = type.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().Reverse()
                .Select(t => t.Identifier.Text + (t.TypeParameterList is { } tp ? "`" + tp.Parameters.Count : ""));
            var ns = string.Join('.', type.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(n => n.Name.ToString()));
            var name = string.Join('/', names);
            yield return new ClassCoverage(ns.Length == 0 ? name : ns + "." + name, ns, type.Identifier.Text, doc.FilePath ?? "", methods);
        }
    }

    static string? Full(string? path) => string.IsNullOrEmpty(path) ? null : Path.GetFullPath(path);
}
