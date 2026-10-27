using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using TestGen.Cli;
using TestGen.Coverage;
using TestGen.Queue;
using TestGen.Tests.Analysis;
using Xunit;

namespace TestGen.Tests.Cli.Orchestration;

public sealed class WorkPlanBuilderTests
{
    static readonly (string, string)[] Prod =
    [
        ("Calc.cs", "namespace Acme.Core; public class Calc { public int Add(int a, int b) => a + b; public int Sub(int a, int b) => a - b; }"),
        ("Parser.cs", "namespace Acme.Core; public class Parser { public int Parse(string s) => int.Parse(s); }"),
        ("Good.cs", "namespace Acme.Core; public class Good { public int One() => 1; }"),
    ];

    // Neither CalcTests nor ParserTests exists; both classes are referenced from one test file => one work item.
    static readonly (string, string)[] Tests =
    [
        ("CoreTests.cs", "namespace Acme.Core.Tests; public class CoreTests { [Xunit.Fact] public void T() { new Acme.Core.Calc(); new Acme.Core.Parser(); } }"),
    ];

    static MethodCoverage Method(string name, int covered, int uncovered) => new(name, "(int,int)",
        [.. Enumerable.Range(1, covered).Select(i => new LineCoverage(i, 1, false, 0, 0)),
         .. Enumerable.Range(100, uncovered).Select(i => new LineCoverage(i, 0, false, 0, 0))]);

    static ClassCoverage Class(string name, params MethodCoverage[] methods) =>
        new($"Acme.Core.{name}", "Acme.Core", name, $"{RoslynTestWorkspace.ProdDir()}/{name}.cs", methods);

    static readonly CoverageSnapshot Coverage = new(DateTimeOffset.UnixEpoch,
    [
        Class("Calc", Method("Add", 1, 3), Method("Sub", 2, 0)),   // 3/6 = 0.5
        Class("Parser", Method("Parse", 1, 1)),                    // 0.5
        Class("Good", Method("One", 9, 1)),                        // 0.9 >= MinCoverage
        Class("Ghost", Method("Boo", 0, 5)),                       // not in the solution
    ]);

    static Task<IReadOnlyList<WorkItem>> Build(Microsoft.CodeAnalysis.Solution s, int minCoverage = 80) =>
        WorkPlanBuilder.BuildAsync(s, Coverage, new TestGenOptions { MinCoverage = minCoverage }, NullLogger.Instance, CancellationToken.None);

    [Fact]
    public async Task Groups_classes_by_target_file_and_filters_by_min_coverage()
    {
        var item = (await Build(RoslynTestWorkspace.Create(Prod, Tests))).ShouldHaveSingleItem();

        item.TargetTestFile.ShouldBe($"{RoslynTestWorkspace.TestDir()}/CoreTests.cs");
        item.Id.ShouldBe(WorkItem.MakeId(item.TargetTestFile));
        item.TestProjectPath.ShouldBe($"{RoslynTestWorkspace.TestDir()}/Acme.Core.Tests.csproj");
        item.Status.ShouldBe(WorkStatus.Pending);
        item.Classes.Select(c => c.FullName).ShouldBe(["Acme.Core.Calc", "Acme.Core.Parser"], ignoreOrder: true);
        item.Classes.Single(c => c.FullName == "Acme.Core.Calc").UncoveredMethods.ShouldBe(["Add"]);
        item.UncoveredLines.ShouldBe(4);
        item.CoverageBefore.ShouldBe(0.5);
    }

    [Fact]
    public async Task Higher_min_coverage_includes_more_classes()
    {
        var items = await Build(RoslynTestWorkspace.Create(Prod, Tests), minCoverage: 95);
        items.SelectMany(i => i.Classes).Select(c => c.FullName).ShouldContain("Acme.Core.Good");
    }

    [Fact]
    public async Task No_test_project_gives_skipped_items()
    {
        var items = await Build(RoslynTestWorkspace.Create(Prod));

        items.Count.ShouldBe(2);
        items.ShouldAllBe(i => i.Status == WorkStatus.Skipped && i.Reason == "no test project for Acme.Core" && i.TestProjectPath == "");
        var calc = items.Single(i => i.Classes[0].FullName == "Acme.Core.Calc");
        calc.TargetTestFile.ShouldBe(calc.Classes[0].SourceFile);
        calc.Id.ShouldBe(WorkItem.MakeId(calc.TargetTestFile));
    }
}
