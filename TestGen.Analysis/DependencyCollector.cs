using Microsoft.CodeAnalysis;

namespace TestGen.Analysis;

public sealed record CtorParam(string Name, string TypeFullName, bool IsInterface, bool IsAbstract);

public sealed record TypeDependencies(IReadOnlyList<CtorParam> CtorParams, IReadOnlyList<string> MemberSignatures);

public static class DependencyCollector
{
    static readonly SymbolDisplayFormat Signature = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeType | SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeRef,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeName
                          | SymbolDisplayParameterOptions.IncludeParamsRefOut | SymbolDisplayParameterOptions.IncludeDefaultValue,
        propertyStyle: SymbolDisplayPropertyStyle.ShowReadWriteDescriptor,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers
                              | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    public static TypeDependencies Collect(INamedTypeSymbol type)
    {
        var ctor = type.InstanceConstructors.Where(c => c.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal)
            .MaxBy(c => c.Parameters.Length);
        if (ctor is null) return new([], []);

        var ctorParams = ctor.Parameters.Select(p => new CtorParam(
            p.Name,
            p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat.WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted)),
            p.Type.TypeKind == TypeKind.Interface,
            p.Type.TypeKind == TypeKind.Class && p.Type.IsAbstract)).ToList();

        var signatures = ctor.Parameters.Select(p => p.Type).OfType<INamedTypeSymbol>()
            .Where(t => t.TypeKind == TypeKind.Interface || (t.TypeKind == TypeKind.Class && t.IsAbstract))
            .Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default)
            .SelectMany(t => Members(t).Select(m => $"{t.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}: {m.ToDisplayString(Signature)}"))
            .Distinct().ToList();
        return new(ctorParams, signatures);
    }

    static IEnumerable<ISymbol> Members(INamedTypeSymbol t) =>
        (t.TypeKind == TypeKind.Interface ? t.AllInterfaces.Prepend(t) : [t])
        .SelectMany(x => x.GetMembers())
        .Where(m => m is IMethodSymbol { MethodKind: MethodKind.Ordinary } or IPropertySymbol)
        .Where(m => t.TypeKind == TypeKind.Interface || (m.DeclaredAccessibility == Accessibility.Public && (m.IsAbstract || m.IsVirtual)));
}
