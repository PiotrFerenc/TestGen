using System.Text.Json;
using Shouldly;
using TestGen.Coverage;
using Xunit;

namespace TestGen.Tests.Coverage;

public class CoverageTests : IDisposable
{
    readonly CoverageSnapshot _snap = CoberturaParser.Parse(CoberturaSample.Xml);
    readonly string _dir = Directory.CreateTempSubdirectory("testgen-cov-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    ClassCoverage Cls(string fullName) => _snap.Classes.Single(c => c.FullName == fullName);

    [Fact]
    public void Parse_CountsClassesAndLines()
    {
        _snap.Classes.Count.ShouldBe(8);
        _snap.TotalLines.ShouldBe(19);
        _snap.CoveredLines.ShouldBe(7);
        _snap.Timestamp.ShouldBe(DateTimeOffset.FromUnixTimeSeconds(1700000000));
        var calc = Cls("App.Core.Calculator");
        calc.TotalLines.ShouldBe(5); // class-level duplicate lines not double counted
        calc.UncoveredLines.ShouldBe(1);
        calc.UncoveredLineNumbers.ShouldBe([16]);
        calc.LineRate.ShouldBe(0.8);
        calc.Namespace.ShouldBe("App.Core");
        calc.Name.ShouldBe("Calculator");
        var add = calc.Methods.Single(m => m.Name == "Add");
        add.IsFullyCovered.ShouldBeTrue();
        add.Signature.ShouldBe("(int, int)");
        (add.StartLine, add.EndLine).ShouldBe((10, 11));
    }

    [Fact]
    public void Parse_Branches()
    {
        var divide = Cls("App.Core.Calculator").Methods.Single(m => m.Name == "Divide");
        var line = divide.Lines.Single(l => l.Number == 15);
        line.IsBranch.ShouldBeTrue();
        (line.BranchesCovered, line.BranchesTotal).ShouldBe((1, 2));
        divide.UncoveredBranches.ShouldBe(1);
        divide.IsFullyCovered.ShouldBeFalse();
        Cls("App.Core.Repository`1").Methods[0].UncoveredBranches.ShouldBe(2);
        _snap.BranchRate.ShouldBe(0.25);
    }

    [Fact]
    public void Parse_MergesPartialClasses()
    {
        var order = Cls("App.Core.Order");
        order.SourceFile.ShouldBe("/src/App.Core/Order.cs");
        order.Methods.Select(m => m.Name).ShouldBe(["Validate", "Total"]);
        order.UncoveredLineNumbers.ShouldBe([4, 7, 8]);
    }

    [Fact]
    public void Parse_SkipsCompilerGeneratedClasses()
    {
        _snap.Classes.ShouldNotContain(c => c.FullName.Contains('<'));
        _snap.Classes.SelectMany(c => c.UncoveredLineNumbers).ShouldNotContain(20);
    }

    [Fact]
    public void DisplayName_NormalizesNestedAndKeepsArity()
    {
        var nested = Cls("App.Core.Outer/Nested");
        nested.DisplayName.ShouldBe("App.Core.Outer.Nested");
        nested.Namespace.ShouldBe("App.Core");
        nested.Name.ShouldBe("Nested");
        Cls("App.Core.Repository`1").DisplayName.ShouldBe("App.Core.Repository`1");
    }

    [Fact]
    public void Exclusions_DefaultsRemoveEachRule()
    {
        var kept = new CoverageExclusions().Apply(_snap).Classes.Select(c => c.FullName).Order();
        kept.ShouldBe(["App.Core.Calculator", "App.Core.Order", "App.Core.Outer/Nested", "App.Core.Repository`1"]);
    }

    [Theory]
    [InlineData("Program")]
    [InlineData("App.Web.Generated.Client")]
    [InlineData("App.Web.Migrations.Init")]
    [InlineData("App.Web.Dtos.UserDto")]
    public void Exclusions_IndividualRules(string fullName)
    {
        var none = new CoverageExclusions { FileGlobs = [], ClassNames = [], NamespaceSegments = [], ExcludeLogicless = false };
        none.IsExcluded(Cls(fullName)).ShouldBeFalse();
        new CoverageExclusions().IsExcluded(Cls(fullName)).ShouldBeTrue();
    }

    [Fact]
    public void IsLogicless_OnlyForAccessorsAndCtors()
    {
        CoverageExclusions.IsLogicless(Cls("App.Web.Dtos.UserDto")).ShouldBeTrue();
        CoverageExclusions.IsLogicless(Cls("App.Core.Calculator")).ShouldBeFalse();
    }

    [Fact]
    public void Json_RoundTrip()
    {
        var path = Path.Combine(_dir, "sub", "before.json");
        CoverageReportWriter.WriteJson(_snap, path);
        var back = CoverageReportWriter.ReadJson(path);
        JsonSerializer.Serialize(back).ShouldBe(JsonSerializer.Serialize(_snap));
        back.TotalLines.ShouldBe(19);
    }

    [Fact]
    public void Html_ContainsClassesAndTotals()
    {
        var path = Path.Combine(_dir, "report.html");
        CoverageReportWriter.WriteHtml(_snap, Improved(), path);
        var html = File.ReadAllText(path);
        html.ShouldContain("App.Core.Outer.Nested");
        html.ShouldContain("App.Core.Repository`1");
        html.ShouldContain("Total");
        html.ShouldContain("36.8%"); // 7/19 before
        html.IndexOf("App.Core.Order").ShouldBeLessThan(html.IndexOf("App.Core.Calculator")); // 3 uncovered before 1
    }

    [Fact]
    public void Html_WithoutAfter()
    {
        var path = Path.Combine(_dir, "before.html");
        CoverageReportWriter.WriteHtml(_snap, null, path);
        File.ReadAllText(path).ShouldContain("App.Core.Calculator");
    }

    [Fact]
    public void Diff_ComputesDeltas()
    {
        var cmp = CoverageDiff.Compare(_snap, Improved());
        var nested = cmp.Classes.Single(d => d.FullName == "App.Core.Outer/Nested");
        (nested.RateBefore, nested.RateAfter, nested.UncoveredBefore, nested.UncoveredAfter).ShouldBe((0.0, 1.0, 2, 0));
        nested.Delta.ShouldBe(1.0);
        cmp.Classes.Single(d => d.FullName == "App.Core.Calculator").Delta.ShouldBe(0);
        cmp.Total.UncoveredBefore.ShouldBe(12);
        cmp.Total.UncoveredAfter.ShouldBe(10);
        cmp.Classes[0].UncoveredBefore.ShouldBe(cmp.Classes.Max(d => d.UncoveredBefore));
    }

    [Fact]
    public void RateFor_ReturnsRateOrNull()
    {
        CoverageDiff.RateFor(_snap, "App.Core.Calculator").ShouldBe(0.8);
        CoverageDiff.RateFor(_snap, "Nope.Missing").ShouldBeNull();
    }

    CoverageSnapshot Improved() => _snap with
    {
        Classes = _snap.Classes.Select(c => c.FullName != "App.Core.Outer/Nested" ? c : c with
        {
            Methods = c.Methods.Select(m => m with { Lines = m.Lines.Select(l => l with { Hits = 1 }).ToList() }).ToList()
        }).ToList()
    };
}
