using System.IO.Enumeration;

namespace TestGen.Coverage;

/// <summary>Default exclusions per PLAN.md. [ExcludeFromCodeCoverage] is already honored by dotnet-coverage.</summary>
public sealed record CoverageExclusions
{
    public IReadOnlyList<string> FileGlobs { get; init; } = ["*.g.cs", "*.Designer.cs", "*.generated.cs"];
    public IReadOnlyList<string> ClassNames { get; init; } = ["Program"];
    /// <summary>Matched against namespace segments and source directory names.</summary>
    public IReadOnlyList<string> NamespaceSegments { get; init; } = ["Migrations"];
    public bool ExcludeLogicless { get; init; } = true;

    static readonly HashSet<string> RecordMembers =
        [".ctor", ".cctor", "Equals", "GetHashCode", "ToString", "PrintMembers", "<Clone>$", "op_Equality", "op_Inequality", "Deconstruct"];

    public CoverageSnapshot Apply(CoverageSnapshot snapshot) =>
        snapshot with { Classes = snapshot.Classes.Where(c => !IsExcluded(c)).ToList() };

    public bool IsExcluded(ClassCoverage c)
    {
        var fileName = Path.GetFileName(c.SourceFile);
        if (FileGlobs.Any(g => FileSystemName.MatchesSimpleExpression(g, fileName))) return true;
        if (ClassNames.Any(n => n == c.Name || n == c.FullName || n == c.DisplayName)) return true;
        var segments = c.Namespace.Split('.').Concat(c.SourceFile.Split('/', '\\'));
        if (segments.Any(s => NamespaceSegments.Contains(s, StringComparer.OrdinalIgnoreCase))) return true;
        return ExcludeLogicless && IsLogicless(c);
    }

    // ponytail: name-based heuristic; a getter with logic still counts as logicless. Use Roslyn (Analysis) if that matters.
    public static bool IsLogicless(ClassCoverage c) =>
        c.Methods.Count > 0 && c.Methods.All(m =>
            m.Name.StartsWith("get_") || m.Name.StartsWith("set_") || RecordMembers.Contains(m.Name));
}
