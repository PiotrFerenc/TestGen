using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

namespace TestGen.Analysis;

public static class CallPathFinder
{
    const int MaxResults = 10;

    public static async Task<IReadOnlyList<string>> FindPathsAsync(Solution solution, IMethodSymbol target, int maxDepth = 4, CancellationToken ct = default)
    {
        var assembly = target.ContainingAssembly;
        var internalsVisible = assembly.GetAttributes().Any(a => a.AttributeClass?.Name == "InternalsVisibleToAttribute");
        var results = new List<string>();

        async Task Walk(IMethodSymbol method, List<IMethodSymbol> chain)
        {
            if (results.Count >= MaxResults) return;
            if (IsEntryPoint(method, internalsVisible))
            {
                var path = string.Join(" -> ", chain.AsEnumerable().Reverse().Select(m => m.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)));
                if (!results.Contains(path)) results.Add(path);
                return;
            }
            if (chain.Count > maxDepth) return;

            var callers = await SymbolFinder.FindCallersAsync((ISymbol?)method.AssociatedSymbol ?? method, solution, ct);
            foreach (var caller in callers)
            {
                if (OwningMethod(caller.CallingSymbol) is not { } m) continue;
                if (!SymbolEqualityComparer.Default.Equals(m.ContainingAssembly, assembly)) continue;
                if (chain.Contains(m, SymbolEqualityComparer.Default)) continue;
                await Walk(m, [.. chain, m]);
            }
        }

        await Walk(target, [target]);
        return results;
    }

    // Lambdas and local functions are attributed to the method that declares them.
    static IMethodSymbol? OwningMethod(ISymbol symbol)
    {
        for (var s = symbol; s is not null; s = s.ContainingSymbol)
            if (s is IMethodSymbol { MethodKind: not (MethodKind.AnonymousFunction or MethodKind.LocalFunction) } m) return m;
        return null;
    }

    static bool IsEntryPoint(IMethodSymbol method, bool internalsVisible)
    {
        for (ISymbol? s = method; s is not null and not INamespaceSymbol; s = s.ContainingSymbol)
        {
            var ok = s.DeclaredAccessibility == Accessibility.Public
                     || (internalsVisible && s.DeclaredAccessibility is Accessibility.Internal or Accessibility.ProtectedOrInternal);
            if (!ok) return false;
        }
        return true;
    }
}
