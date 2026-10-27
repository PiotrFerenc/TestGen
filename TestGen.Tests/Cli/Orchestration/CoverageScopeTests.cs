using Shouldly;
using TestGen.Cli;
using TestGen.Coverage;
using TestGen.Tests.Analysis;
using Xunit;

namespace TestGen.Tests.Cli.Orchestration;

public sealed class CoverageScopeTests
{
    static readonly (string, string)[] Prod =
    [
        ("Util.cs", """
            namespace Acme.Core;
            public static class Util
            {
                public static string Rev(string s)
                {
                    var c = s.ToCharArray();
                    return new string(c);
                }
                public class Inner { public int One() => 1; }
            }
            public interface IThing { void Do(); }
            """),
    ];

    static readonly (string, string)[] Tests = [("UtilTests.cs", "namespace Acme.Core.Tests; public class UtilTests { [Xunit.Fact] public void T() { } }")];

    static ClassCoverage Class(string name, string file, int hits) =>
        new(name, "", name.Split('.')[^1], file, [new MethodCoverage("M", "()", [new LineCoverage(1, hits, false, 0, 0)])]);

    [Fact]
    public void Drops_test_classes_and_synthesizes_unloaded_production_project()
    {
        var solution = RoslynTestWorkspace.Create(Prod, Tests);
        var snapshot = new CoverageSnapshot(DateTimeOffset.UnixEpoch,
            [Class("Acme.Core.Tests.UtilTests", $"{RoslynTestWorkspace.TestDir()}/UtilTests.cs", 1),
             Class("Acme.Core.Tests.MergedLaterTests", $"{RoslynTestWorkspace.TestDir()}/MergedLaterTests.cs", 1)]);

        var scoped = CoverageScope.Apply(snapshot, solution);

        scoped.Classes.Select(c => c.FullName).Order().ShouldBe(["Acme.Core.Util", "Acme.Core.Util/Inner"]);
        var util = scoped.Classes.Single(c => c.FullName == "Acme.Core.Util");
        util.Methods.Single().Name.ShouldBe("Rev");
        util.Methods.Single().Lines.Select(l => l.Number).ShouldBe([6, 7]);
        util.CoveredLines.ShouldBe(0);
    }

    [Fact]
    public void Keeps_reported_production_project_as_is()
    {
        var solution = RoslynTestWorkspace.Create(Prod, Tests);
        var snapshot = new CoverageSnapshot(DateTimeOffset.UnixEpoch, [Class("Acme.Core.Util", $"{RoslynTestWorkspace.ProdDir()}/Util.cs", 0)]);

        CoverageScope.Apply(snapshot, solution).Classes.ShouldBe(snapshot.Classes);
    }
}
