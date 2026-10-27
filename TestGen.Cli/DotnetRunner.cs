using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace TestGen.Cli;

public sealed record DotnetResult(int ExitCode, string Output)
{
    public bool Success => ExitCode == 0;

    /// <summary>Lines that look like compiler/test errors, for the LLM repair loop.</summary>
    public IReadOnlyList<string> ErrorLines => Output
        .Split('\n')
        .Select(l => l.TrimEnd())
        .Where(l => l.Contains(": error ", StringComparison.Ordinal)
                 || l.Contains("[FAIL]", StringComparison.Ordinal)
                 || l.Contains("Failed ", StringComparison.Ordinal)
                 || l.Contains("Assert", StringComparison.Ordinal)
                 || l.Contains("Exception", StringComparison.Ordinal))
        .Distinct()
        .ToList();
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

    public Task<DotnetResult> BuildAsync(string projectOrSolution, CancellationToken ct, TimeSpan? timeout = null)
        => RunAsync($"build \"{projectOrSolution}\" --nologo -v q", Path.GetDirectoryName(projectOrSolution)!, timeout ?? DefaultBuildTimeout, ct);

    public Task<DotnetResult> TestAsync(string projectOrSolution, TimeSpan timeout, CancellationToken ct, string? filter = null, bool noBuild = false)
    {
        var sb = new StringBuilder($"test \"{projectOrSolution}\" --nologo -v q");
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

        var output = (await stdout) + (await stderr);
        return new DotnetResult(process.ExitCode, output);
    }
}
