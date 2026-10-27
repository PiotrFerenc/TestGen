using System.Text.Json;
using Shouldly;
using TestGen.Cli.Merge;
using TestGen.Cli.Report;
using TestGen.Coverage;
using TestGen.Llm;
using TestGen.Queue;
using Xunit;

namespace TestGen.Tests.Cli.Report;

public sealed class FinalReportTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "testgen-report-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    static CoverageSnapshot Snapshot(int covered, int total) => new(DateTimeOffset.UnixEpoch,
    [
        new ClassCoverage("Acme.Foo", "Acme", "Foo", "/repo/Foo.cs",
            [new MethodCoverage("Bar", "()", Enumerable.Range(1, total).Select(n => new LineCoverage(n, n <= covered ? 1 : 0, false, 0, 0)).ToList())]),
    ]);

    static (RunState, MergeReport, TokenUsage) Data()
    {
        var state = new RunState
        {
            SolutionPath = "/repo/All.sln",
            Items =
            [
                new() { Id = "foo-1", Status = WorkStatus.Done, Attempts = 1, Branch = "testgen/foo-1", CoverageBefore = 0.25, CoverageAfter = 0.75, PromptTokens = 100, CompletionTokens = 20,
                    Classes = [new WorkClass("Acme.Foo", "/repo/Foo.cs", 3, ["Bar"])] },
                new() { Id = "baz-2", Status = WorkStatus.Failed, Attempts = 3, Reason = new string('x', 100) + " | pipe [markup]",
                    Classes = [new WorkClass("Acme.Baz", "/repo/Baz.cs", 5, [])] },
            ],
        };
        var tokens = new TokenUsage();
        tokens.AddPrompt(100);
        tokens.AddCompletion(20);
        return (state, new MergeReport(["foo-1"], [("baz-2", "merge conflict")], true, true), tokens);
    }

    [Fact]
    public void Write_produces_json_and_markdown()
    {
        var (state, merge, tokens) = Data();

        FinalReport.Write(state, Snapshot(1, 4), Snapshot(3, 4), merge, tokens, _dir);

        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "summary.json")));
        var root = json.RootElement;
        root.GetProperty("Totals").GetProperty("Done").GetInt32().ShouldBe(1);
        root.GetProperty("Totals").GetProperty("Failed").GetInt32().ShouldBe(1);
        root.GetProperty("Coverage").GetProperty("BeforePercent").GetDouble().ShouldBe(25);
        root.GetProperty("Coverage").GetProperty("AfterPercent").GetDouble().ShouldBe(75);
        root.GetProperty("Coverage").GetProperty("DeltaPercent").GetDouble().ShouldBe(50);
        root.GetProperty("Merged")[0].GetString().ShouldBe("foo-1");
        root.GetProperty("Rejected")[0].GetProperty("Reason").GetString().ShouldBe("merge conflict");
        root.GetProperty("Tokens").GetProperty("Total").GetInt64().ShouldBe(120);
        var item = root.GetProperty("Items")[0];
        item.GetProperty("Classes")[0].GetString().ShouldBe("Acme.Foo");
        item.GetProperty("Status").GetString().ShouldBe("Done");
        item.GetProperty("CoverageAfter").GetDouble().ShouldBe(0.75);

        var md = File.ReadAllText(Path.Combine(_dir, "summary.md"));
        md.ShouldContain("| foo-1 | Acme.Foo | Done | 1 | 25% | 75% | 120 |");
        md.ShouldContain("\\| pipe");
        md.ShouldContain("| Done | 1 |");
        md.ShouldContain("this run");
        md.ShouldContain("0..1 fractions");
    }

    [Fact]
    public void Print_does_not_throw_with_markup_like_reasons_and_missing_after()
    {
        var (state, merge, tokens) = Data();

        Should.NotThrow(() => FinalReport.Print(state, Snapshot(1, 4), null, merge, tokens));
        Should.NotThrow(() => FinalReport.Print(state, Snapshot(1, 4), Snapshot(3, 4), merge, tokens));
    }
}
