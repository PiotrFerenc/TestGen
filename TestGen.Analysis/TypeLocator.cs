using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace TestGen.Analysis;

public static class TypeLocator
{
    static readonly SymbolDisplayFormat MetadataLike = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.ExpandNullable);

    public static async Task<INamedTypeSymbol?> FindTypeAsync(Solution solution, string coberturaName, CancellationToken ct)
    {
        // ponytail: compiler-generated parts (Foo/<Bar>d__3) are cut to the owning type; C#-style generic names (Foo<T>) are not handled.
        var cut = coberturaName.IndexOf("/<", StringComparison.Ordinal);
        var metadataName = (cut >= 0 ? coberturaName[..cut] : coberturaName).Replace('/', '+');
        foreach (var project in SolutionLoader.ProductionProjects(solution))
        {
            var compilation = await project.GetCompilationAsync(ct);
            var type = compilation?.GetTypeByMetadataName(metadataName);
            if (type is not null && type.DeclaringSyntaxReferences.Length > 0) return type;
        }
        return null;
    }

    public static IReadOnlyList<TypeDeclarationSyntax> Declarations(INamedTypeSymbol type) =>
        type.DeclaringSyntaxReferences.Select(r => r.GetSyntax()).OfType<TypeDeclarationSyntax>().ToList();

    public static MethodDeclarationSyntax? FindMethod(INamedTypeSymbol type, string methodName, string? coberturaSignature)
    {
        methodName = Regex.Replace(methodName, @"`\d+$", ""); // cobertura: Map`1
        var candidates = type.GetMembers(methodName).OfType<IMethodSymbol>()
            .Select(m => (Symbol: m, Syntax: m.DeclaringSyntaxReferences.Select(r => r.GetSyntax()).OfType<MethodDeclarationSyntax>().FirstOrDefault()))
            .Where(c => c.Syntax is not null).ToList();
        if (candidates.Count <= 1 || coberturaSignature is null) return candidates.FirstOrDefault().Syntax;

        var wanted = SplitParameters(coberturaSignature).Select(Normalize).ToList();
        var byCount = candidates.Where(c => c.Symbol.Parameters.Length == wanted.Count).ToList();
        var exact = byCount.FirstOrDefault(c => c.Symbol.Parameters.Select(p => Normalize(p.Type.ToDisplayString(MetadataLike))).SequenceEqual(wanted));
        return (exact.Syntax ?? byCount.FirstOrDefault().Syntax) ?? candidates[0].Syntax;
    }

    public static (int Start, int End) LineSpan(SyntaxNode node)
    {
        var span = node.SyntaxTree.GetLineSpan(node.Span);
        return (span.StartLinePosition.Line + 1, span.EndLinePosition.Line + 1);
    }

    /// <summary>Usings + namespace + every declaration of the type (all partial parts), each wrapped in its containing-type headers.</summary>
    public static string FullSource(INamedTypeSymbol type)
    {
        var decls = Declarations(type);
        if (decls.Count == 0) throw new InvalidOperationException($"{type} has no source declaration");
        var usings = decls.SelectMany(d => d.SyntaxTree.GetCompilationUnitRoot().Usings
                .Concat(d.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse().SelectMany(n => n.Usings)))
            .Select(u => u.ToString()).Distinct();
        var ns = type.ContainingNamespace.IsGlobalNamespace ? "" : $"namespace {type.ContainingNamespace.ToDisplayString()};\n\n";
        return string.Join("\n", usings) + "\n\n" + ns + string.Join("\n\n", decls.Select(Wrapped)) + "\n";
    }

    static string Wrapped(TypeDeclarationSyntax decl)
    {
        var text = decl.ToFullString().Trim('\r', '\n');
        foreach (var outer in decl.Ancestors().OfType<TypeDeclarationSyntax>())
        {
            var header = decl.SyntaxTree.GetText().ToString(TextSpan.FromBounds(outer.SpanStart, outer.OpenBraceToken.SpanStart)).TrimEnd();
            var indent = new string(' ', outer.GetLeadingTrivia().ToString().Split('\n')[^1].Length);
            text = $"{indent}{header}\n{indent}{{\n{text}\n{indent}}}";
        }
        return text;
    }

    static string Normalize(string typeName) =>
        Regex.Replace(typeName, @"`\d+|&|\s", "").Replace('+', '.').Replace('/', '.');

    static List<string> SplitParameters(string signature)
    {
        var inner = signature.Trim().TrimStart('(').TrimEnd(')');
        var result = new List<string>();
        if (inner.Length == 0) return result;
        int depth = 0, start = 0;
        for (var i = 0; i < inner.Length; i++)
        {
            if (inner[i] is '<' or '[') depth++;
            else if (inner[i] is '>' or ']') depth--;
            else if (inner[i] == ',' && depth == 0) { result.Add(inner[start..i]); start = i + 1; }
        }
        result.Add(inner[start..]);
        return result;
    }
}
