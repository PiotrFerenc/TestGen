using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.Formatting;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace TestGen.Analysis;

public static class TestInserter
{
    public static async Task<string> InsertAsync(Document existingTestDocument, CompilationUnitSyntax snippet, CancellationToken ct)
    {
        var editor = await DocumentEditor.CreateAsync(existingTestDocument, ct);
        var root = (CompilationUnitSyntax)editor.OriginalRoot;
        var cls = root.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault()
                  ?? throw new InvalidOperationException($"No class in {existingTestDocument.FilePath}");

        editor.InsertMembers(cls, cls.Members.Count, snippet.Members.Select(m => m.WithAdditionalAnnotations(Formatter.Annotation)));

        var existing = root.DescendantNodes().OfType<UsingDirectiveSyntax>().Select(Key).ToHashSet();
        var missing = snippet.Usings.Where(u => existing.Add(Key(u)))
            .Select(u => u.WithoutTrivia().WithTrailingTrivia(ElasticCarriageReturnLineFeed).WithAdditionalAnnotations(Formatter.Annotation))
            .ToArray();
        if (missing.Length > 0) editor.ReplaceNode(root, (node, _) => ((CompilationUnitSyntax)node).AddUsings(missing));

        var formatted = await Formatter.FormatAsync(editor.GetChangedDocument(), Formatter.Annotation, cancellationToken: ct);
        return (await formatted.GetTextAsync(ct)).ToString();
    }

    public static string CreateNewFile(string className, string ns, TestConventions conventions, CompilationUnitSyntax snippet)
    {
        var usings = conventions.Usings.Select(n => UsingDirective(ParseName(n)))
            .Concat(snippet.Usings.Select(u => u.WithoutTrivia()))
            .DistinctBy(Key).ToList();
        var order = ConventionDetector.SortUsings(usings.Select(u => u.Name?.ToString() ?? "").Distinct(), conventions.UsingsOrder).ToList();
        usings = usings.OrderBy(u => order.IndexOf(u.Name?.ToString() ?? "")).ToList();

        var cls = ClassDeclaration(className).AddModifiers(Token(SyntaxKind.PublicKeyword)).WithMembers(snippet.Members);
        MemberDeclarationSyntax top = string.IsNullOrEmpty(ns) ? cls
            : conventions.FileScopedNamespaces ? FileScopedNamespaceDeclaration(ParseName(ns)).AddMembers(cls)
            : NamespaceDeclaration(ParseName(ns)).AddMembers(cls);
        return CompilationUnit().WithUsings(List(usings)).AddMembers(top).NormalizeWhitespace(eol: "\n").ToFullString() + "\n";
    }

    static string Key(UsingDirectiveSyntax u) => Regex.Replace(u.WithoutTrivia().ToString(), @"\s+", " ");
}
