using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace TestGen.Coverage;

public interface ICoverageRunner
{
    Task<bool> IsToolAvailableAsync(CancellationToken ct);

    /// <summary>Runs tests under dotnet-coverage; returns the full path of the cobertura file.</summary>
    Task<string> CollectAsync(string solutionOrProjectPath, string outputFile, TimeSpan timeout, CancellationToken ct,
        string? extraTestArgs = null, bool noBuild = false);
}

public sealed class CoverageRunner(ILogger<CoverageRunner>? logger = null, string toolPath = "dotnet-coverage") : ICoverageRunner
{
    readonly ILogger _log = logger ?? NullLogger<CoverageRunner>.Instance;
    static readonly TimeSpan OutputGrace = TimeSpan.FromSeconds(30);

    public async Task<bool> IsToolAvailableAsync(CancellationToken ct)
    {
        try
        {
            var (exit, _) = await RunAsync(["--version"], Environment.CurrentDirectory, TimeSpan.FromSeconds(30), ct);
            return exit == 0;
        }
        catch (CoverageException ex)
        {
            _log.LogWarning("dotnet-coverage not available: {Message}", ex.Message);
            return false;
        }
    }

    public async Task<string> CollectAsync(string solutionOrProjectPath, string outputFile, TimeSpan timeout, CancellationToken ct,
        string? extraTestArgs = null, bool noBuild = false)
    {
        var target = Path.GetFullPath(solutionOrProjectPath);
        var output = Path.GetFullPath(outputFile);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.Delete(output);
        var workDir = Directory.Exists(target) ? target : Path.GetDirectoryName(target)!;

        var testCmd = $"dotnet test \"{target}\"{(noBuild ? " --no-build" : "")}{(string.IsNullOrWhiteSpace(extraTestArgs) ? "" : " " + extraTestArgs)}";
        _log.LogInformation("Collecting coverage: {Command}", testCmd);

        var (exit, log) = await RunAsync(["collect", testCmd, "-f", "cobertura", "-o", output], workDir, timeout, ct);
        if (exit != 0) throw new CoverageException($"dotnet-coverage exited with {exit}:\n{Tail(log)}");
        if (!File.Exists(output)) throw new CoverageException($"Coverage file not created: {output}\n{Tail(log)}");
        return output;
    }

    async Task<(int ExitCode, string Output)> RunAsync(string[] args, string workDir, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(toolPath)
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        var sb = new StringBuilder();
        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        p.Exited += (_, _) => exited.TrySetResult();
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        try { p.Start(); }
        catch (Exception ex) { throw new CoverageException($"Cannot start '{toolPath}': {ex.Message}", ex); }
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            // Not WaitForExitAsync: it also waits for pipe EOF, which a grandchild can hold open forever.
            await exited.Task.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            ct.ThrowIfCancellationRequested();
            throw new CoverageException($"dotnet-coverage timed out after {timeout}:\n{Tail(sb.ToString())}");
        }
        // Flush async output, bounded: a grandchild holding the pipes must not hang us.
        using var grace = CancellationTokenSource.CreateLinkedTokenSource(ct);
        grace.CancelAfter(OutputGrace);
        try { await p.WaitForExitAsync(grace.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            ct.ThrowIfCancellationRequested();
        }
        lock (sb) return (p.ExitCode, sb.ToString());
    }

    static string Tail(string s, int lines = 40) => string.Join('\n', s.Split('\n').TakeLast(lines));
}
