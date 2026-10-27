using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace TestGen.Analysis;

public sealed record ValidationResult(bool IsValid, IReadOnlyList<string> Errors, CompilationUnitSyntax? Parsed);

public static class SnippetValidator
{
    static readonly Regex Fence = new(@"```[\w#+-]*[^\S\n]*\n(.*?)```", RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>
    /// Parses an LLM snippet (loose members or a wrapping class) into a compilation unit holding only usings and members.
    /// Conventions are accepted for future rule tuning; all known frameworks are recognised regardless.
    /// </summary>
    public static ValidationResult Validate(string snippet, TestConventions conventions)
    {
        var match = Fence.Match(snippet);
        var code = match.Success ? match.Groups[1].Value : snippet.Trim();

        var cu = CSharpSyntaxTree.ParseText(code).GetCompilationUnitRoot();
        var cls = cu.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault();
        SyntaxList<MemberDeclarationSyntax> members;
        IEnumerable<Diagnostic> diagnostics;
        if (cls is not null)
        {
            members = cls.Members;
            diagnostics = cu.GetDiagnostics();
        }
        else
        {
            // Loose members: re-parse wrapped in a class, usings stay in the outer parse.
            var restStart = cu.Usings.Count > 0 ? cu.Usings.Last().Span.End : 0;
            var wrapped = CSharpSyntaxTree.ParseText("class __Snippet\n{\n" + code[restStart..] + "\n}\n").GetCompilationUnitRoot();
            members = ((ClassDeclarationSyntax)wrapped.Members[0]).Members;
            diagnostics = cu.GetDiagnostics().Where(d => d.Location.SourceSpan.End <= restStart).Concat(wrapped.GetDiagnostics());
        }

        var errors = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => $"{d.Id}: {d.GetMessage()} (line {d.Location.GetLineSpan().StartLinePosition.Line + 1})").ToList();
        if (errors.Count > 0) return new(false, errors, null);

        var usings = cu.DescendantNodes().OfType<UsingDirectiveSyntax>().Select(u => u.WithoutTrivia());
        var parsed = CompilationUnit().WithUsings(List(usings)).WithMembers(members);
        if (!TestSyntax.TestMethods(parsed).Any()) errors.Add("No test methods found (expected [Fact], [Theory], [Test], [TestCase] or [TestMethod]).");
        errors.AddRange(TestSyntax.Check(parsed, requireAssertions: true));
        return new(errors.Count == 0, errors, parsed);
    }
}
