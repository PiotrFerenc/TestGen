using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace TestGen.Git;

public sealed record GitResult(int ExitCode, string StdOut, string StdErr);

public sealed record WorktreeInfo(string Path, string? Head, string? Branch);

public sealed class GitException(string args, int exitCode, string stderr)
    : Exception($"git {args} failed with exit code {exitCode}: {stderr.Trim()}")
{
    public string Args { get; } = args;
    public int ExitCode { get; } = exitCode;
    public string StdErr { get; } = stderr;
}

public interface IGitRunner
{
    string WorkDir { get; }
    Task<GitResult> RunAsync(string args, CancellationToken ct, bool throwOnError = true);
    Task<bool> IsRepositoryAsync(CancellationToken ct);
    Task<bool> IsCleanAsync(CancellationToken ct);
    Task<string> GetRepoRootAsync(CancellationToken ct);
    Task<string> HeadShaAsync(CancellationToken ct);
    Task<string?> CurrentBranchAsync(CancellationToken ct);
    Task<bool> BranchExistsAsync(string name, CancellationToken ct);
    Task CreateBranchAsync(string name, string startPoint, CancellationToken ct);
    Task DeleteBranchAsync(string name, bool force, CancellationToken ct);
    Task CheckoutAsync(string refName, CancellationToken ct);
    Task AddAsync(IEnumerable<string> paths, CancellationToken ct);
    Task AddAllAsync(CancellationToken ct);
    Task<string> CommitAsync(string message, CancellationToken ct, bool allowEmpty = false);
    Task<IGitRunner> WorktreeAddAsync(string path, string branch, string startPoint, CancellationToken ct);
    Task WorktreeRemoveAsync(string path, bool force, CancellationToken ct);
    Task WorktreePruneAsync(CancellationToken ct);
    Task<IReadOnlyList<WorktreeInfo>> WorktreeListAsync(CancellationToken ct);
    Task MergeAsync(string branch, CancellationToken ct, string? message = null);
    Task CherryPickAsync(string sha, CancellationToken ct);
    Task<IReadOnlyList<string>> DiffNameOnlyAsync(string from, string to, CancellationToken ct);
    Task<IReadOnlyList<string>> LogShasAsync(string range, CancellationToken ct);
    Task ResetHardAsync(string refName, CancellationToken ct);
}

public sealed partial class GitRunner(string workDir, ILogger<GitRunner>? logger = null) : IGitRunner
{
    private readonly ILogger<GitRunner> _logger = logger ?? NullLogger<GitRunner>.Instance;

    public string WorkDir { get; } = Path.GetFullPath(workDir);

    // Worktree/branch mutations on one repo must not interleave (prune can drop admin dirs of a worktree being added).
    // Keyed by the main runner's WorkDir; runners from WorktreeAddAsync inherit it.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> RepoLocks = new();
    private string LockKey { get; init; } = Path.GetFullPath(workDir);
    private static readonly TimeSpan OutputGrace = TimeSpan.FromSeconds(30);

    public async Task<GitResult> RunAsync(string args, CancellationToken ct, bool throwOnError = true)
    {
        var psi = new ProcessStartInfo("git", args)
        {
            WorkingDirectory = WorkDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["LC_ALL"] = "C";

        _logger.LogDebug("git {Args} (in {WorkDir})", args, WorkDir);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start git");
        p.StandardInput.Close();
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        try
        {
            await p.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }

        // A grandchild (hook, credential helper) may keep the pipes open after git exits.
        try
        {
            await Task.WhenAll(stdout, stderr).WaitAsync(OutputGrace, ct);
        }
        catch (Exception e) when (e is TimeoutException or OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            ct.ThrowIfCancellationRequested();
            throw new GitException(args, p.ExitCode, $"output pipes still open {OutputGrace.TotalSeconds}s after exit");
        }

        var result = new GitResult(p.ExitCode, await stdout, await stderr);
        if (result.ExitCode != 0)
        {
            _logger.LogDebug("git {Args} exited {Code}: {StdErr}", args, result.ExitCode, result.StdErr.Trim());
            if (throwOnError) throw new GitException(args, result.ExitCode, result.StdErr);
        }
        return result;
    }

    public async Task<bool> IsRepositoryAsync(CancellationToken ct)
    {
        if (!Directory.Exists(WorkDir)) return false;
        var r = await RunAsync("rev-parse --is-inside-work-tree", ct, throwOnError: false);
        return r.ExitCode == 0 && r.StdOut.Trim() == "true";
    }

    public async Task<bool> IsCleanAsync(CancellationToken ct) =>
        (await RunAsync("status --porcelain", ct)).StdOut.Trim().Length == 0;

    public async Task<string> GetRepoRootAsync(CancellationToken ct) =>
        Path.GetFullPath((await RunAsync("rev-parse --show-toplevel", ct)).StdOut.Trim());

    public async Task<string> HeadShaAsync(CancellationToken ct) =>
        (await RunAsync("rev-parse HEAD", ct)).StdOut.Trim();

    /// <summary>Short branch name, or null when HEAD is detached.</summary>
    public async Task<string?> CurrentBranchAsync(CancellationToken ct)
    {
        var r = await RunAsync("symbolic-ref --short -q HEAD", ct, throwOnError: false);
        return r.ExitCode == 0 ? r.StdOut.Trim() : null;
    }

    public async Task<bool> BranchExistsAsync(string name, CancellationToken ct) =>
        (await RunAsync($"show-ref --verify --quiet {Q("refs/heads/" + name)}", ct, throwOnError: false)).ExitCode == 0;

    public Task CreateBranchAsync(string name, string startPoint, CancellationToken ct) =>
        RunLockedAsync($"branch {Q(name)} {Q(startPoint)}", ct);

    public Task DeleteBranchAsync(string name, bool force, CancellationToken ct) =>
        RunLockedAsync($"branch {(force ? "-D" : "-d")} {Q(name)}", ct);

    public Task CheckoutAsync(string refName, CancellationToken ct) =>
        RunAsync($"checkout {Q(refName)}", ct);

    public Task AddAsync(IEnumerable<string> paths, CancellationToken ct) =>
        RunAsync("add -- " + string.Join(' ', paths.Select(Q)), ct);

    public Task AddAllAsync(CancellationToken ct) => RunAsync("add -A", ct);

    public async Task<string> CommitAsync(string message, CancellationToken ct, bool allowEmpty = false)
    {
        await RunAsync($"{await IdentityArgsAsync(ct)}commit {(allowEmpty ? "--allow-empty " : "")}-m {Q(message)}", ct);
        return await HeadShaAsync(ct);
    }

    /// <summary>Creates a new branch <paramref name="branch"/> at <paramref name="startPoint"/> checked out in <paramref name="path"/>.</summary>
    public async Task<IGitRunner> WorktreeAddAsync(string path, string branch, string startPoint, CancellationToken ct)
    {
        var full = Path.GetFullPath(path);
        await RunLockedAsync($"worktree add -b {Q(branch)} {Q(full)} {Q(startPoint)}", ct);
        return new GitRunner(full, logger) { LockKey = LockKey };
    }

    public Task WorktreeRemoveAsync(string path, bool force, CancellationToken ct) =>
        RunLockedAsync($"worktree remove {(force ? "--force " : "")}{Q(Path.GetFullPath(path))}", ct);

    public Task WorktreePruneAsync(CancellationToken ct) => RunLockedAsync("worktree prune", ct);

    private async Task<GitResult> RunLockedAsync(string args, CancellationToken ct)
    {
        var gate = RepoLocks.GetOrAdd(LockKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try { return await RunAsync(args, ct); }
        finally { gate.Release(); }
    }

    public async Task<IReadOnlyList<WorktreeInfo>> WorktreeListAsync(CancellationToken ct)
    {
        var list = new List<WorktreeInfo>();
        string? path = null, head = null, branch = null;
        foreach (var line in (await RunAsync("worktree list --porcelain", ct)).StdOut.Split('\n').Append(""))
        {
            var l = line.TrimEnd('\r');
            if (l.StartsWith("worktree ")) path = l[9..];
            else if (l.StartsWith("HEAD ")) head = l[5..];
            else if (l.StartsWith("branch ")) branch = l[7..].Replace("refs/heads/", "");
            else if (l.Length == 0 && path != null)
            {
                list.Add(new WorktreeInfo(Path.GetFullPath(path), head, branch));
                path = head = branch = null;
            }
        }
        return list;
    }

    /// <summary>Merges with --no-ff. On failure (conflict) the merge is aborted and GitException is thrown.</summary>
    public async Task MergeAsync(string branch, CancellationToken ct, string? message = null)
    {
        var msg = message is null ? "--no-edit" : $"-m {Q(message)}";
        var args = $"{await IdentityArgsAsync(ct)}merge --no-ff {msg} {Q(branch)}";
        var r = await RunAsync(args, ct, throwOnError: false);
        if (r.ExitCode == 0) return;
        await RunAsync("merge --abort", ct, throwOnError: false);
        throw new GitException(args, r.ExitCode, r.StdErr + r.StdOut);
    }

    /// <summary>On failure (conflict) the cherry-pick is aborted and GitException is thrown.</summary>
    public async Task CherryPickAsync(string sha, CancellationToken ct)
    {
        var args = $"{await IdentityArgsAsync(ct)}cherry-pick {Q(sha)}";
        var r = await RunAsync(args, ct, throwOnError: false);
        if (r.ExitCode == 0) return;
        await RunAsync("cherry-pick --abort", ct, throwOnError: false);
        throw new GitException(args, r.ExitCode, r.StdErr + r.StdOut);
    }

    /// <summary>Changed paths relative to the repo root.</summary>
    public async Task<IReadOnlyList<string>> DiffNameOnlyAsync(string from, string to, CancellationToken ct) =>
        Lines((await RunAsync($"diff --name-only {Q(from)} {Q(to)} --", ct)).StdOut);

    public async Task<IReadOnlyList<string>> LogShasAsync(string range, CancellationToken ct) =>
        Lines((await RunAsync($"log --format=%H {Q(range)} --", ct)).StdOut);

    public Task ResetHardAsync(string refName, CancellationToken ct) =>
        RunAsync($"reset --hard {Q(refName)}", ct);

    /// <summary>Makes a single valid ref component: lowercase, [a-z0-9._-] only, no "..", no leading/trailing '.'/'-', no ".lock" suffix.</summary>
    public static string SanitizeBranchName(string name)
    {
        var s = InvalidChars().Replace(name.ToLowerInvariant(), "-");
        s = Dashes().Replace(s, "-");
        s = Dots().Replace(s, ".");
        while (s.EndsWith(".lock")) s = s[..^5];
        s = s.Trim('-', '.');
        if (s.Length == 0) throw new ArgumentException($"'{name}' has no usable characters for a branch name", nameof(name));
        return s;
    }

    // Respect configured identity; only fill in what's missing so commits work on bare CI boxes.
    private async Task<string> IdentityArgsAsync(CancellationToken ct)
    {
        var name = (await RunAsync("config user.name", ct, throwOnError: false)).ExitCode == 0 ? "" : "-c user.name=testgen ";
        var email = (await RunAsync("config user.email", ct, throwOnError: false)).ExitCode == 0 ? "" : "-c user.email=testgen@local ";
        return name + email;
    }

    private static IReadOnlyList<string> Lines(string s) =>
        s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // Quotes one argument for ProcessStartInfo.Arguments parsing (MSVCRT rules, also used by .NET on Unix).
    private static string Q(string arg)
    {
        var sb = new System.Text.StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\') { backslashes++; continue; }
            sb.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes);
            backslashes = 0;
            sb.Append(c);
        }
        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }

    [GeneratedRegex(@"[^a-z0-9._-]+")] private static partial Regex InvalidChars();
    [GeneratedRegex(@"-{2,}")] private static partial Regex Dashes();
    [GeneratedRegex(@"\.{2,}")] private static partial Regex Dots();
}
