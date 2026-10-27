using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using TestGen.Analysis;
using TestGen.Coverage;
using TestGen.Queue;

namespace TestGen.Cli;

/// <summary>Turns under-covered classes into work items, one per target test file.</summary>
public static class WorkPlanBuilder
{
    public static async Task<IReadOnlyList<WorkItem>> BuildAsync(Solution solution, CoverageSnapshot coverage, TestGenOptions options, ILogger logger, CancellationToken ct)
    {
        var located = new List<(ClassCoverage Class, string TargetFile, string TestProject, string? SkipReason)>();
        foreach (var cls in coverage.Classes.Where(c => c.LineRate * 100 < options.MinCoverage && c.UncoveredLines > 0))
        {
            var type = await TypeLocator.FindTypeAsync(solution, cls.FullName, ct);
            if (type is null)
            {
                logger.LogDebug("No source type for {Class}, skipped", cls.FullName);
                continue;
            }
            var target = await TestLocator.FindAsync(solution, type, ct);
            if (target is null)
            {
                var project = solution.GetDocument(type.DeclaringSyntaxReferences.FirstOrDefault()?.SyntaxTree)?.Project.Name ?? type.ContainingAssembly.Name;
                located.Add((cls, cls.SourceFile, "", $"no test project for {project}"));
            }
            else located.Add((cls, target.FilePath, target.TestProject.FilePath ?? "", null));
        }

        return located.GroupBy(l => l.TargetFile).Select(g => new WorkItem
        {
            Id = WorkItem.MakeId(g.Key),
            TargetTestFile = g.Key,
            TestProjectPath = g.First().TestProject,
            Classes = g.Select(l => new WorkClass(l.Class.FullName, l.Class.SourceFile, l.Class.UncoveredLines,
                l.Class.Methods.Where(m => !m.IsFullyCovered).Select(m => m.Name).Distinct().ToList())).ToList(),
            UncoveredLines = g.Sum(l => l.Class.UncoveredLines),
            CoverageBefore = g.Min(l => l.Class.LineRate),
            Status = g.First().SkipReason is null ? WorkStatus.Pending : WorkStatus.Skipped,
            Reason = g.First().SkipReason,
        }).ToList();
    }
}
