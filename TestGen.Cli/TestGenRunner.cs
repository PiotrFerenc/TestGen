using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Spectre.Console;
using TestGen.Analysis;
using TestGen.Cli.Bootstrap;
using TestGen.Cli.Merge;
using TestGen.Cli.Report;
using TestGen.Cli.Worker;
using TestGen.Coverage;
using TestGen.Git;
using TestGen.Llm;
using TestGen.Queue;

namespace TestGen.Cli;

/// <summary>The whole PLAN.md flow, top to bottom. Returns the process exit code (0 ok, 4 final build/test red).</summary>
public sealed class TestGenRunner(
    IGitRunner git, IDotnetRunner dotnet, ICoverageRunner coverage, ILlmClient llm, PromptBuilder prompts, TokenUsage tokens,
    IOptions<TestGenOptions> options, ILoggerFactory loggers)
{
    public async Task<int> RunAsync(CancellationToken ct)
    {
        var o = options.Value;
        var log = loggers.CreateLogger<TestGenRunner>();
        var solutionPath = Path.GetFullPath(o.SolutionPath);

        ProgressUi.Section("Preflight");
        var repoRoot = await new Preflight(git, dotnet, coverage, o, loggers.CreateLogger<Preflight>()).RunAsync(ct);
        AnsiConsole.MarkupLine($"Repository: {Markup.Escape(repoRoot)}");
        Directory.CreateDirectory(o.WorkDir);
        // Keep scratch files (state, worktrees, reports) out of git so the tree stays clean and bootstrap commits never pick them up.
        var ignore = Path.Combine(o.WorkDir, ".gitignore");
        if (!File.Exists(ignore)) await File.WriteAllTextAsync(ignore, "*\n", ct);

        // Resume: state.json present => keep testgen/result as is; otherwise start it fresh from the current HEAD.
        var store = new StateStore(StateStore.DefaultPath(o.SolutionDir));
        var previous = await store.LoadAsync(ct);
        var onResult = await git.CurrentBranchAsync(ct) == TestGenOptions.ResultBranch;
        if (previous is null && !onResult && await git.BranchExistsAsync(TestGenOptions.ResultBranch, ct))
            await git.DeleteBranchAsync(TestGenOptions.ResultBranch, force: true, ct);
        if (!await git.BranchExistsAsync(TestGenOptions.ResultBranch, ct))
            await git.CreateBranchAsync(TestGenOptions.ResultBranch, "HEAD", ct);
        if (!onResult) await git.CheckoutAsync(TestGenOptions.ResultBranch, ct);
        if (previous is not null) AnsiConsole.MarkupLine($"[yellow]Resuming run started {previous.StartedAt:u}[/]");

        ProgressUi.Section("Coverage (before)");
        var beforeJson = Path.Combine(o.WorkDir, "coverage-before.json");
        CoverageSnapshot before;
        if (previous is not null && File.Exists(beforeJson)) before = CoverageReportWriter.ReadJson(beforeJson);
        else
        {
            var xml = await coverage.CollectAsync(solutionPath, Path.Combine(o.WorkDir, "coverage-before.xml"), o.TestTimeout, ct);
            before = new CoverageExclusions().Apply(CoberturaParser.ParseFile(xml));
        }

        ProgressUi.Section("Analysis");
        var solution = await SolutionLoader.OpenAsync(solutionPath, log, ct);
        var conventions = SolutionLoader.TestProjects(solution).Any()
            ? await ConventionDetector.DetectAsync(solution, ct)
            : TestConventions.Default(o.DefaultStack.Framework, o.DefaultStack.Mocking, o.DefaultStack.Assertions);
        AnsiConsole.WriteLine(conventions.Summarize());

        ProgressUi.Section("Bootstrap");
        var boot = await new TestProjectBootstrapper(git, dotnet, o, loggers.CreateLogger<TestProjectBootstrapper>()).BootstrapAsync(solution, conventions, ct);
        if (boot.CreatedProjects.Count > 0)
        {
            AnsiConsole.MarkupLine($"Created test projects: {Markup.Escape(string.Join(", ", boot.CreatedProjects))}");
            solution = await SolutionLoader.OpenAsync(solutionPath, log, ct);
        }
        var baseCommit = await git.HeadShaAsync(ct);
        // Scoped after bootstrap: test classes are not targets, and projects never loaded by tests still need work items.
        before = CoverageScope.Apply(before, solution);
        CoverageReportWriter.WriteJson(before, beforeJson);
        CoverageReportWriter.WriteHtml(before, null, Path.Combine(o.WorkDir, "coverage-before.html"));
        PrintTotals("before", before);

        ProgressUi.Section("Plan");
        var items = await WorkPlanBuilder.BuildAsync(solution, before, o, log, ct);
        var state = previous is null ? WorkPlanner.Plan(solutionPath, baseCommit, items) : WorkPlanner.Resume(previous, items, o.MaxRetries);
        await store.SaveAsync(state, ct);
        AnsiConsole.MarkupLine(string.Join("  ", state.Totals().Select(t => $"{t.Key}: {t.Value}")));

        ProgressUi.Section("Workers");
        var processor = new WorkItemProcessor(solution, conventions, before, git, llm, prompts, coverage, dotnet, o, loggers.CreateLogger<WorkItemProcessor>());
        var pool = new WorkerPool(processor, store, loggers.CreateLogger<WorkerPool>());
        using (new ProgressUi(pool, state))
            state = await pool.RunAsync(state, o.Parallel > 0 ? o.Parallel : WorkerPool.DefaultParallelism, ct);
        ct.ThrowIfCancellationRequested(); // pool returns normally on cancel; state is saved, the next run resumes

        ProgressUi.Section("Merge");
        var merge = await new ResultMerger(git, dotnet, o, loggers.CreateLogger<ResultMerger>()).MergeAsync(state, ct);
        await store.SaveAsync(state, ct);

        ProgressUi.Section("Coverage (after)");
        CoverageSnapshot? after = null;
        try
        {
            var xml = await coverage.CollectAsync(solutionPath, Path.Combine(o.WorkDir, "coverage-after.xml"), o.TestTimeout, ct);
            after = CoverageScope.Apply(new CoverageExclusions().Apply(CoberturaParser.ParseFile(xml)), solution);
            CoverageReportWriter.WriteJson(after, Path.Combine(o.WorkDir, "coverage-after.json"));
            CoverageReportWriter.WriteHtml(before, after, Path.Combine(o.WorkDir, "coverage-report.html"));
            PrintTotals("after", after);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning("Coverage after failed: {Message}", ex.Message);
        }

        ProgressUi.Section("Summary");
        FinalReport.Write(state, before, after, merge, tokens, o.WorkDir);
        FinalReport.Print(state, before, after, merge, tokens);
        return merge.FullBuildOk && merge.FullTestOk ? 0 : 4;
    }

    static void PrintTotals(string label, CoverageSnapshot s) =>
        AnsiConsole.MarkupLine($"Coverage {label}: [bold]{s.LineRate:P1}[/] ({s.CoveredLines}/{s.TotalLines} lines, {s.Classes.Count} classes)");
}
