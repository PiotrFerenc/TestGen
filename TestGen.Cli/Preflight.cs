using Microsoft.Extensions.Logging;
using TestGen.Coverage;
using TestGen.Git;
using TestGen.Queue;

namespace TestGen.Cli;

public sealed class PreflightException(string message) : Exception(message);

/// <summary>Checks that must pass before any coverage run or LLM call. LLM options are validated by the host.</summary>
public sealed class Preflight(IGitRunner git, IDotnetRunner dotnet, ICoverageRunner coverage, TestGenOptions options, ILogger<Preflight> logger)
{
    /// <summary>Returns the repository root.</summary>
    public async Task<string> RunAsync(CancellationToken ct)
    {
        var solution = options.SolutionPath;
        if (!File.Exists(solution))
            throw new PreflightException($"Solution file not found: {solution}");

        if (!await git.IsRepositoryAsync(ct))
            throw new PreflightException($"{options.SolutionDir} is not inside a git repository.");

        // A dirty tree is allowed only when resuming an interrupted run: state.json exists and we are on the result branch
        // (an interrupted run may leave bootstrap/merge leftovers there). Anywhere else uncommitted changes would be mixed into the result.
        if (!await git.IsCleanAsync(ct))
        {
            var resuming = File.Exists(StateStore.DefaultPath(options.SolutionDir))
                           && await git.CurrentBranchAsync(ct) == TestGenOptions.ResultBranch;
            if (!resuming)
                throw new PreflightException("Working tree has uncommitted changes. Commit or stash them first.");
            logger.LogWarning("Working tree is dirty, but a previous run on {Branch} is being resumed.", TestGenOptions.ResultBranch);
        }

        if (!await coverage.IsToolAvailableAsync(ct))
            throw new PreflightException("dotnet-coverage not found. Install it: dotnet tool install -g dotnet-coverage");

        var build = await dotnet.BuildAsync(solution, ct);
        if (!build.Success)
            throw new PreflightException("Baseline build failed:\n" + FirstErrors(build));

        var test = await dotnet.TestAsync(solution, options.TestTimeout, ct, noBuild: true);
        if (!test.Success)
            throw new PreflightException("Baseline tests failed:\n" + FirstErrors(test));

        return await git.GetRepoRootAsync(ct);
    }

    static string FirstErrors(DotnetResult r)
    {
        var lines = r.ErrorLines.Count > 0 ? r.ErrorLines : r.Output.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).TakeLast(20).ToList();
        return string.Join('\n', lines.Take(20));
    }
}
