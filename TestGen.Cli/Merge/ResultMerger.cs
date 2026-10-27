using Microsoft.Extensions.Logging;
using TestGen.Git;
using TestGen.Queue;

namespace TestGen.Cli.Merge;

public sealed record MergeReport(IReadOnlyList<string> MergedIds, IReadOnlyList<(string Id, string Reason)> Rejected, bool FullBuildOk, bool FullTestOk);

/// <summary>Merges successful worker branches one by one into <see cref="TestGenOptions.ResultBranch"/>, keeping the solution green.</summary>
public sealed class ResultMerger(IGitRunner repoGit, IDotnetRunner dotnet, TestGenOptions options, ILogger<ResultMerger> logger)
{
    public async Task<MergeReport> MergeAsync(RunState state, CancellationToken ct)
    {
        var branch = await repoGit.CurrentBranchAsync(ct);
        if (branch != TestGenOptions.ResultBranch)
            throw new InvalidOperationException($"Merge must run on {TestGenOptions.ResultBranch}, but the current branch is {branch ?? "(detached)"}.");

        var merged = new List<string>();
        var rejected = new List<(string, string)>();
        (bool Build, bool Test)? last = null; // results that describe the current HEAD

        // ponytail: full build+test per merge is slow but correct; batch merges if it hurts.
        foreach (var item in state.Items.Where(i => i is { Status: WorkStatus.Done, Branch: not null }))
        {
            var sha = await repoGit.HeadShaAsync(ct);
            try
            {
                await repoGit.MergeAsync(item.Branch!, ct, $"testgen: merge {item.Id}");
            }
            catch (GitException e)
            {
                logger.LogWarning("Merge of {Branch} conflicted: {Message}", item.Branch, e.Message);
                Reject(item, "merge conflict");
                continue;
            }

            var (buildOk, testOk, error) = await VerifyAsync(ct);
            if (buildOk && testOk)
            {
                merged.Add(item.Id);
                last = (true, true);
                await repoGit.DeleteBranchAsync(item.Branch!, force: true, ct);
                logger.LogInformation("Merged {Id}", item.Id);
            }
            else
            {
                await repoGit.ResetHardAsync(sha, ct);
                last = null; // HEAD is back at sha; its state is not what we just measured
                Reject(item, $"solution build/test failed after merge: {error}");
            }
        }

        if (last is null)
        {
            var (b, t, _) = await VerifyAsync(ct);
            last = (b, t);
        }
        return new MergeReport(merged, rejected, last.Value.Build, last.Value.Test);

        void Reject(WorkItem item, string reason)
        {
            logger.LogWarning("Rejected {Id}: {Reason}", item.Id, reason);
            rejected.Add((item.Id, reason));
            item.Status = WorkStatus.Failed;
            item.Reason = reason;
        }
    }

    async Task<(bool Build, bool Test, string Error)> VerifyAsync(CancellationToken ct)
    {
        var build = await dotnet.BuildAsync(options.SolutionPath, ct);
        if (!build.Success) return (false, false, FirstError(build));
        var test = await dotnet.TestAsync(options.SolutionPath, options.TestTimeout, ct, noBuild: true);
        return (true, test.Success, test.Success ? "" : FirstError(test));
    }

    static string FirstError(DotnetResult r) =>
        r.ErrorLines.FirstOrDefault()?.Trim()
        ?? r.Output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0)
        ?? $"exit code {r.ExitCode}";
}
