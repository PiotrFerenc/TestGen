using System.Globalization;
using System.Text;
using System.Text.Json;
using Spectre.Console;
using TestGen.Cli.Merge;
using TestGen.Coverage;
using TestGen.Llm;
using TestGen.Queue;

namespace TestGen.Cli.Report;

/// <summary>End-of-run summary: <c>summary.json</c> + <c>summary.md</c> on disk and Spectre tables on stdout.</summary>
public static class FinalReport
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static void Write(RunState state, CoverageSnapshot before, CoverageSnapshot? after, MergeReport merge, TokenUsage tokens, string workDir)
    {
        Directory.CreateDirectory(workDir);
        var summary = new
        {
            state.SolutionPath,
            state.BaseCommit,
            state.StartedAt,
            Totals = state.Totals().ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
            // Totals in percent (0..100); per-item CoverageBefore/After are 0..1 fractions.
            Coverage = new
            {
                BeforePercent = Percent(before),
                AfterPercent = after is null ? (double?)null : Percent(after),
                DeltaPercent = after is null ? (double?)null : Math.Round(Percent(after) - Percent(before), 2),
            },
            Merged = merge.MergedIds,
            // Done items whose branch was consumed by a merge, across all runs (resume-safe).
            MergedTotal = state.Items.Where(i => i.Status == WorkStatus.Done && i.Branch is null).Select(i => i.Id).ToList(),
            Rejected = merge.Rejected.Select(r => new { r.Id, r.Reason }),
            merge.FullBuildOk,
            merge.FullTestOk,
            Tokens = new { tokens.PromptTokens, tokens.CompletionTokens, tokens.Total, tokens.Requests },
            Items = state.Items.Select(i => new
            {
                i.Id,
                Classes = i.Classes.Select(c => c.FullName),
                Status = i.Status.ToString(),
                i.Reason,
                i.Attempts,
                i.Branch,
                i.CoverageBefore,
                i.CoverageAfter,
                i.PromptTokens,
                i.CompletionTokens,
            }),
        };
        File.WriteAllText(Path.Combine(workDir, "summary.json"), JsonSerializer.Serialize(summary, Json));

        var md = new StringBuilder()
            .AppendLine("# TestGen summary").AppendLine()
            .AppendLine("_Merged and token counts cover this run (invocation) only. summary.json: `Coverage.*Percent` are 0..100, per-item `CoverageBefore`/`CoverageAfter` are 0..1 fractions._").AppendLine()
            .AppendLine($"- Solution: `{state.SolutionPath}`")
            .AppendLine($"- Coverage: {CoverageLine(before, after)}")
            .AppendLine($"- Full build: {(merge.FullBuildOk ? "ok" : "FAILED")}, full test: {(merge.FullTestOk ? "ok" : "FAILED")}")
            .AppendLine($"- Merged this run: {merge.MergedIds.Count}, rejected: {merge.Rejected.Count}; merged in total: {state.Items.Count(i => i.Status == WorkStatus.Done && i.Branch is null)}")
            .AppendLine($"- Tokens this run: {tokens.PromptTokens} prompt + {tokens.CompletionTokens} completion = {tokens.Total} ({tokens.Requests} requests)")
            .AppendLine().AppendLine("| Status | Count |").AppendLine("|---|---|");
        foreach (var (status, count) in state.Totals()) md.AppendLine($"| {status} | {count} |");
        md.AppendLine().AppendLine("| Id | Classes | Status | Attempts | Coverage before | Coverage after | Tokens | Reason |").AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var i in state.Items)
            md.AppendLine($"| {i.Id} | {Cell(string.Join(", ", i.Classes.Select(c => c.FullName)))} | {i.Status} | {i.Attempts} | {Rate(i.CoverageBefore)} | {Rate(i.CoverageAfter)} | {i.PromptTokens + i.CompletionTokens} | {Cell(i.Reason ?? "")} |");
        File.WriteAllText(Path.Combine(workDir, "summary.md"), md.ToString());
    }

    public static void Print(RunState state, CoverageSnapshot before, CoverageSnapshot? after, MergeReport merge, TokenUsage tokens)
    {
        AnsiConsole.Write(new Panel(Markup.Escape($"Coverage {CoverageLine(before, after)}\nMerged {merge.MergedIds.Count}, rejected {merge.Rejected.Count}; full build {(merge.FullBuildOk ? "ok" : "FAILED")}, full test {(merge.FullTestOk ? "ok" : "FAILED")}"))
            .Header("TestGen summary"));

        var totals = new Table().AddColumns("Status", "Count");
        foreach (var (status, count) in state.Totals()) totals.AddRow(status.ToString(), count.ToString(CultureInfo.InvariantCulture));
        AnsiConsole.Write(totals);

        var items = new Table().AddColumns("Id", "Classes", "Status", "Attempts", "Before", "After", "Reason");
        foreach (var i in state.Items)
            items.AddRow(
                Markup.Escape(i.Id),
                Markup.Escape(string.Join(", ", i.Classes.Select(c => c.FullName))),
                Markup.Escape(i.Status.ToString()),
                i.Attempts.ToString(CultureInfo.InvariantCulture),
                Rate(i.CoverageBefore),
                Rate(i.CoverageAfter),
                Markup.Escape(Truncate(i.Reason ?? "", 60)));
        AnsiConsole.Write(items);

        AnsiConsole.MarkupLine(Markup.Escape($"Tokens: {tokens.PromptTokens} prompt + {tokens.CompletionTokens} completion = {tokens.Total} ({tokens.Requests} requests)"));
    }

    static double Percent(CoverageSnapshot s) => Math.Round(s.LineRate * 100, 2);

    static string CoverageLine(CoverageSnapshot before, CoverageSnapshot? after) => after is null
        ? string.Create(CultureInfo.InvariantCulture, $"{Percent(before):0.##}% -> n/a")
        : string.Create(CultureInfo.InvariantCulture, $"{Percent(before):0.##}% -> {Percent(after):0.##}% ({Percent(after) - Percent(before):+0.##;-0.##;0}%)");

    /// <summary>Item coverage is a 0..1 rate (CoverageDiff.RateFor).</summary>
    static string Rate(double? rate) => rate is { } r ? (r * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%" : "-";

    static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 3)] + "...";

    static string Cell(string s) => s.Replace("|", "\\|").ReplaceLineEndings(" ");
}
