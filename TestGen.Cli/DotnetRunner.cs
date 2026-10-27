using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace TestGen.Cli;

public sealed record DotnetResult(int ExitCode, string Output)
{
    public bool Success => ExitCode == 0;

    /// <summary>Lines that look like compiler/test errors, plus each failed test's "Error Message:" block (expected/actual), for the LLM repair loop.</summary>
    public IReadOnlyList<string> ErrorLines
    {
        get
        {
            var lines = new List<string>();
            var seen = new HashSet<string>();
            var inMessage = false;
            foreach (var l in Output.Split('\n').Select(l => l.TrimEnd()))
            {
                var t = l.TrimStart();
                if (t == "Error Message:") { inMessage = true; lines.Add(l); continue; }
                if (t.StartsWith("Stack Trace:", StringComparison.Ordinal) || t.StartsWith("Standard Output Messages:", StringComparison.Ordinal)
                    || t.StartsWith("Failed ", StringComparison.Ordinal) || t.StartsWith("Passed ", StringComparison.Ordinal)) inMessage = false;
                if (inMessage) lines.Add(l); // not de-duplicated: "should be"/"but was" blocks repeat across failures
                else if (IsErrorLine(l) && seen.Add(l)) lines.Add(l);
            }
            return lines;
        }
    }

    static bool IsErrorLine(string l) =>
        l.Contains(": error ", StringComparison.Ordinal)
        || l.Contains("[FAIL]", StringComparison.Ordinal)
        || l.Contains("Failed ", StringComparison.Ordinal)
        || l.Contains("Assert", StringComparison.Ordinal)
        || l.Contains("Exception", StringComparison.Ordinal);
}

/// <summary>Runs <c>dotnet build/test/restore</c> through Process with a timeout.</summary>
public interface IDotnetRunner
{
    Task<DotnetResult> RunAsync(string args, string workDir, TimeSpan timeout, CancellationToken ct);
    Task<DotnetResult> BuildAsync(string projectOrSolution, CancellationToken ct, TimeSpan? timeout = null);
    Task<DotnetResult> TestAsync(string projectOrSolution, TimeSpan timeout, CancellationToken ct, string? filter = null, bool noBuild = false);
    Task<DotnetResult> RestoreAsync(string projectOrSolution, CancellationToken ct);
}

public sealed class DotnetRunner(ILogger<DotnetRunner>? logger = null) : IDotnetRunner
{
    private static readonly TimeSpan DefaultBuildTimeout = TimeSpan.FromMinutes(15);
    static readonly TimeSpan StreamDrainTimeout = TimeSpan.FromSeconds(30);

    public Task<DotnetResult> BuildAsync(string projectOrSolution, CancellationToken ct, TimeSpan? timeout = null)
        => RunAsync($"build \"{projectOrSolution}\" --nologo -v q", Path.GetDirectoryName(projectOrSolution)!, timeout ?? DefaultBuildTimeout, ct);

    public Task<DotnetResult> TestAsync(string projectOrSolution, TimeSpan timeout, CancellationToken ct, string? filter = null, bool noBuild = false)
    {
        // No "-v q": quiet verbosity drops the "Error Message:" block (expected/actual), which the repair loop needs.
        var sb = new StringBuilder($"test \"{projectOrSolution}\" --nologo");
        if (noBuild) sb.Append(" --no-build");
        if (!string.IsNullOrWhiteSpace(filter)) sb.Append($" --filter \"{filter}\"");
        return RunAsync(sb.ToString(), Path.GetDirectoryName(projectOrSolution)!, timeout, ct);
    }

    public Task<DotnetResult> RestoreAsync(string projectOrSolution, CancellationToken ct)
        => RunAsync($"restore \"{projectOrSolution}\" --nologo -v q", Path.GetDirectoryName(projectOrSolution)!, DefaultBuildTimeout, ct);

    public async Task<DotnetResult> RunAsync(string args, string workDir, TimeSpan timeout, CancellationToken ct)
    {
        logger?.LogDebug("dotnet {Args} (in {Dir})", args, workDir);
        var psi = new ProcessStartInfo("dotnet", args)
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_NOLOGO"] = "1";
        psi.Environment["MSBUILDTERMINALLOGGER"] = "off";

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start dotnet.");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            ct.ThrowIfCancellationRequested();
            return new DotnetResult(-1, $"dotnet {args} timed out after {timeout}.");
        }

        return new DotnetResult(process.ExitCode, await DrainAsync(stdout, stderr, StreamDrainTimeout, ct, () =>
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            logger?.LogWarning("dotnet {Args}: output streams still open {Timeout} after exit", args, StreamDrainTimeout);
        }));
    }

    /// <summary>A grandchild (e.g. a build server) can keep the pipes open after exit: bound the read, then return what completed.</summary>
    public static async Task<string> DrainAsync(Task<string> stdout, Task<string> stderr, TimeSpan timeout, CancellationToken ct, Action? onTimeout = null)
    {
        try { await Task.WhenAll(stdout, stderr).WaitAsync(timeout, ct); }
        catch (TimeoutException) { onTimeout?.Invoke(); }
        static string Read(Task<string> t) => t.IsCompletedSuccessfully ? t.Result : "";
        return Read(stdout) + Read(stderr);
    }
}
