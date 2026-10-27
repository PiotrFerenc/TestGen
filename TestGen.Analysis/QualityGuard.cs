using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TestGen.Analysis;

public static class QualityGuard
{
    public static IReadOnlyList<string> CheckOnlyAdditions(string originalTestFile, string newTestFile)
    {
        var before = CSharpSyntaxTree.ParseText(originalTestFile).GetCompilationUnitRoot();
        var after = CSharpSyntaxTree.ParseText(newTestFile).GetCompilationUnitRoot();
        var errors = new List<string>();

        var afterUsings = after.DescendantNodes().OfType<UsingDirectiveSyntax>().ToList();
        foreach (var u in before.DescendantNodes().OfType<UsingDirectiveSyntax>())
            if (!afterUsings.Any(x => SyntaxFactory.AreEquivalent(u, x)))
                errors.Add($"Removed using: {u.ToString().Trim()}");

        var afterTypes = after.DescendantNodes().OfType<BaseTypeDeclarationSyntax>().Select(t => t.Identifier.Text).ToHashSet();
        foreach (var t in before.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            if (!afterTypes.Contains(t.Identifier.Text))
                errors.Add($"Removed or renamed type: {t.Identifier.Text}");

        var afterMembers = Members(after).ToList();
        foreach (var m in Members(before))
            if (!afterMembers.Any(x => x.GetType() == m.GetType() && SyntaxFactory.AreEquivalent(m, x, topLevel: false)))
                errors.Add($"Removed or modified member: {Describe(m)}");
        return errors;
    }

    public static IReadOnlyList<string> CheckOnlyTestProjectsTouched(IEnumerable<string> changedFilesRelativeToRepo, IEnumerable<string> testProjectDirsRelativeToRepo)
    {
        var dirs = testProjectDirsRelativeToRepo.Select(d => Norm(d).TrimEnd('/') + "/").ToList();
        return changedFilesRelativeToRepo
            .Where(f => !dirs.Any(d => Norm(f).StartsWith(d, StringComparison.Ordinal)))
            .Select(f => $"File outside test projects changed: {f}").ToList();
    }

    public static IReadOnlyList<string> CheckForbiddenPatterns(string testFileText) =>
        TestSyntax.Check(CSharpSyntaxTree.ParseText(testFileText).GetRoot(), requireAssertions: false).ToList();

    static string Norm(string path) => path.Replace('\\', '/').TrimStart('.', '/');

    static IEnumerable<MemberDeclarationSyntax> Members(CompilationUnitSyntax root) =>
        root.DescendantNodes().OfType<MemberDeclarationSyntax>().Where(m => m is not (BaseTypeDeclarationSyntax or BaseNamespaceDeclarationSyntax or DelegateDeclarationSyntax));

    static string Describe(MemberDeclarationSyntax m) => m switch
    {
        MethodDeclarationSyntax x => x.Identifier.Text + "()",
        ConstructorDeclarationSyntax x => x.Identifier.Text + " constructor",
        PropertyDeclarationSyntax x => x.Identifier.Text,
        BaseFieldDeclarationSyntax x => string.Join(", ", x.Declaration.Variables.Select(v => v.Identifier.Text)),
        _ => m.ToString().Split('\n')[0].Trim(),
    };
}
