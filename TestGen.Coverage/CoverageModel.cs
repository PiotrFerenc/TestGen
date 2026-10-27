namespace TestGen.Coverage;

public sealed record LineCoverage(int Number, int Hits, bool IsBranch, int BranchesCovered, int BranchesTotal);

public sealed record MethodCoverage(string Name, string Signature, IReadOnlyList<LineCoverage> Lines)
{
    public IReadOnlyList<int> UncoveredLineNumbers => Lines.Where(l => l.Hits == 0).Select(l => l.Number).Distinct().Order().ToList();
    public int UncoveredBranches => Lines.Sum(l => l.BranchesTotal - l.BranchesCovered);
    public double LineRate => Lines.Count == 0 ? 1 : (double)Lines.Count(l => l.Hits > 0) / Lines.Count;
    public bool IsFullyCovered => Lines.All(l => l.Hits > 0 && l.BranchesCovered == l.BranchesTotal);
    public int StartLine => Lines.Count == 0 ? 0 : Lines.Min(l => l.Number);
    public int EndLine => Lines.Count == 0 ? 0 : Lines.Max(l => l.Number);
}

public sealed record ClassCoverage(string FullName, string Namespace, string Name, string SourceFile, IReadOnlyList<MethodCoverage> Methods)
{
    /// <summary>Cobertura nested separator '/' replaced by '.'; generic arity suffix (`1) kept.</summary>
    public string DisplayName => FullName.Replace('/', '.');
    public int TotalLines => Methods.Sum(m => m.Lines.Count);
    public int CoveredLines => Methods.Sum(m => m.Lines.Count(l => l.Hits > 0));
    public double LineRate => TotalLines == 0 ? 1 : (double)CoveredLines / TotalLines;
    public int UncoveredLines => TotalLines - CoveredLines;
    public IReadOnlyList<int> UncoveredLineNumbers => Methods.SelectMany(m => m.UncoveredLineNumbers).Distinct().Order().ToList();
}

public sealed record CoverageSnapshot(DateTimeOffset Timestamp, IReadOnlyList<ClassCoverage> Classes)
{
    public int TotalLines => Classes.Sum(c => c.TotalLines);
    public int CoveredLines => Classes.Sum(c => c.CoveredLines);
    public int UncoveredLines => TotalLines - CoveredLines;
    public double LineRate => TotalLines == 0 ? 1 : (double)CoveredLines / TotalLines;

    public double BranchRate
    {
        get
        {
            var lines = Classes.SelectMany(c => c.Methods).SelectMany(m => m.Lines).ToList();
            var total = lines.Sum(l => l.BranchesTotal);
            return total == 0 ? 1 : (double)lines.Sum(l => l.BranchesCovered) / total;
        }
    }
}

public sealed class CoverageException(string message, Exception? inner = null) : Exception(message, inner);
