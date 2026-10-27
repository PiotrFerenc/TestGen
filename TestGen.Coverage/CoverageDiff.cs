namespace TestGen.Coverage;

public sealed record ClassDelta(string FullName, double RateBefore, double RateAfter, int UncoveredBefore, int UncoveredAfter)
{
    public double Delta => RateAfter - RateBefore;
}

public sealed record CoverageComparison(IReadOnlyList<ClassDelta> Classes, ClassDelta Total);

public static class CoverageDiff
{
    /// <summary>Classes missing on one side copy the other side's values (no delta). Sorted by UncoveredBefore desc.</summary>
    public static CoverageComparison Compare(CoverageSnapshot before, CoverageSnapshot after)
    {
        var b = before.Classes.ToDictionary(c => c.FullName);
        var a = after.Classes.ToDictionary(c => c.FullName);
        var classes = b.Keys.Union(a.Keys)
            .Select(name =>
            {
                var x = b.GetValueOrDefault(name) ?? a[name];
                var y = a.GetValueOrDefault(name) ?? x;
                return new ClassDelta(name, x.LineRate, y.LineRate, x.UncoveredLines, y.UncoveredLines);
            })
            .OrderByDescending(d => d.UncoveredBefore).ThenBy(d => d.FullName)
            .ToList();
        return new CoverageComparison(classes,
            new ClassDelta("Total", before.LineRate, after.LineRate, before.UncoveredLines, after.UncoveredLines));
    }

    public static double? RateFor(CoverageSnapshot snapshot, string classFullName) =>
        snapshot.Classes.FirstOrDefault(c => c.FullName == classFullName)?.LineRate;
}
