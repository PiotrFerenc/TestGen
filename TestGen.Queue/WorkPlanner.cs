namespace TestGen.Queue;

public static class WorkPlanner
{
    public static RunState Plan(string solutionPath, string baseCommit, IEnumerable<WorkItem> items) => new()
    {
        SolutionPath = solutionPath,
        BaseCommit = baseCommit,
        StartedAt = DateTimeOffset.UtcNow,
        Items = items.OrderByDescending(i => i.UncoveredLines).ToList(),
    };

    /// <summary>
    /// Merges a fresh plan into a previous run: Done/Skipped kept as-is, Running/Failed/Pending rescheduled as Pending
    /// with Attempts, tokens, Branch and coverage carried over, unless Attempts >= maxRetries (then Failed). Items missing from fresh are dropped.
    /// </summary>
    public static RunState Resume(RunState previous, IEnumerable<WorkItem> fresh, int maxRetries)
    {
        var old = previous.Items.ToDictionary(i => i.Id);
        var items = fresh.Select(f =>
        {
            if (!old.TryGetValue(f.Id, out var p)) return f;
            if (p.Status is WorkStatus.Done or WorkStatus.Skipped) return p;
            f.Attempts = p.Attempts;
            f.CoverageBefore ??= p.CoverageBefore;
            f.CoverageAfter ??= p.CoverageAfter;
            f.Branch ??= p.Branch;
            f.PromptTokens += p.PromptTokens;
            f.CompletionTokens += p.CompletionTokens;
            if (p.Attempts >= maxRetries)
            {
                f.Status = WorkStatus.Failed;
                f.Reason = p.Reason ?? $"max retries ({maxRetries}) reached";
            }
            else f.Status = WorkStatus.Pending;
            return f;
        });

        return new RunState
        {
            SolutionPath = previous.SolutionPath,
            BaseCommit = previous.BaseCommit,
            StartedAt = previous.StartedAt,
            Items = items.OrderByDescending(i => i.UncoveredLines).ToList(),
        };
    }
}
