using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;

namespace TestGen.Analysis;

public sealed record TestTarget(Project TestProject, string FilePath, bool Exists, string? TestClassName, string Namespace);

public static class TestLocator
{
    static readonly string[] Suffixes = ["Tests", "Test", "Spec", "Specs", "Should"];

    public static async Task<TestTarget?> FindAsync(Solution solution, INamedTypeSymbol type, CancellationToken ct)
    {
        var prodDoc = solution.GetDocument(type.DeclaringSyntaxReferences.FirstOrDefault()?.SyntaxTree);
        var prodName = prodDoc?.Project.Name ?? type.ContainingAssembly.Name;
        var tests = SolutionLoader.TestProjects(solution)
            .OrderByDescending(p => p.Name.StartsWith(prodName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (tests.Count == 0) return null;

        // 1) by name
        var names = Suffixes.Select(s => type.Name + s).ToList();
        foreach (var project in tests)
        {
            var compilation = await project.GetCompilationAsync(ct);
            var hit = compilation?.GetSymbolsWithName(names.Contains, SymbolFilter.Type, ct).OfType<INamedTypeSymbol>()
                .Where(t => t.DeclaringSyntaxReferences.Length > 0)
                .OrderBy(t => names.IndexOf(t.Name)).FirstOrDefault();
            if (hit is not null)
                return new(project, hit.DeclaringSyntaxReferences[0].SyntaxTree.FilePath, true, hit.Name, NamespaceOf(hit.ContainingNamespace));
        }

        // 2) by references
        var docs = tests.SelectMany(p => p.Documents).ToImmutableHashSet();
        var refs = await SymbolFinder.FindReferencesAsync(type, solution, docs, ct);
        var best = refs.SelectMany(r => r.Locations).GroupBy(l => l.Document.Id).MaxBy(g => g.Count());
        if (best is not null)
        {
            var doc = solution.GetDocument(best.Key)!;
            var root = await doc.GetSyntaxRootAsync(ct);
            var cls = root?.FindNode(best.First().Location.SourceSpan).AncestorsAndSelf().OfType<ClassDeclarationSyntax>().LastOrDefault()
                      ?? root?.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault();
            return new(doc.Project, doc.FilePath ?? doc.Name, true, cls?.Identifier.Text, cls is null ? "" : SyntaxNamespace(cls));
        }

        // 3) propose a new file
        var target = tests.FirstOrDefault(p => p.Name.Equals(prodName + ".Tests", StringComparison.OrdinalIgnoreCase))
                     ?? tests.FirstOrDefault(p => p.Name.StartsWith(prodName + ".", StringComparison.OrdinalIgnoreCase))
                     ?? (tests.Count == 1 ? tests[0] : null);
        if (target is null) return null;

        var folders = prodDoc?.Folders ?? [];
        var testDir = Path.GetDirectoryName(target.FilePath) ?? "";
        var path = Path.Combine([testDir, .. folders, type.Name + "Tests.cs"]);
        var defaultNs = string.IsNullOrEmpty(target.DefaultNamespace) ? target.Name : target.DefaultNamespace;
        var ns = await MirrorsProductionNamespacesAsync(target, defaultNs, ct)
            ? NamespaceOf(type.ContainingNamespace)
            : string.Join('.', new[] { defaultNs }.Concat(folders));
        return new(target, path, false, null, ns);
    }

    // Neighbouring test files declare namespaces outside the test project's root namespace => they mirror production namespaces.
    static async Task<bool> MirrorsProductionNamespacesAsync(Project testProject, string defaultNs, CancellationToken ct)
    {
        foreach (var doc in testProject.Documents)
        {
            var root = await doc.GetSyntaxRootAsync(ct);
            var ns = root?.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString();
            if (ns is not null) return !ns.StartsWith(defaultNs, StringComparison.Ordinal);
        }
        return false;
    }

    static string NamespaceOf(INamespaceSymbol ns) => ns.IsGlobalNamespace ? "" : ns.ToDisplayString();

    internal static string SyntaxNamespace(SyntaxNode node) =>
        string.Join('.', node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(n => n.Name.ToString()));
}
