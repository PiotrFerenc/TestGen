using Shouldly;
using TestGen.Git;
using Xunit;

namespace TestGen.Tests.Git;

public sealed class GitRunnerTests : IAsyncLifetime
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "testgen-tests", Guid.NewGuid().ToString("N"));
    private string Repo => Path.Combine(_root, "repo");
    private GitRunner _git = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Repo);
        _git = new GitRunner(Repo);
        await _git.RunAsync("init -q -b main", Ct);
        await _git.RunAsync("config user.name tester", Ct);
        await _git.RunAsync("config user.email tester@example.com", Ct);
        Write("README.md", "hello");
        await _git.AddAllAsync(Ct);
        await _git.CommitAsync("init", Ct);
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_root))
        {
            foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_root, true);
        }
        return Task.CompletedTask;
    }

    private void Write(string rel, string content, string? dir = null) =>
        File.WriteAllText(Path.Combine(dir ?? Repo, rel), content);

    [Fact]
    public async Task IsRepository_TrueForRepo_FalseForPlainDir()
    {
        (await _git.IsRepositoryAsync(Ct)).ShouldBeTrue();
        var plain = Directory.CreateDirectory(Path.Combine(_root, "plain")).FullName;
        (await new GitRunner(plain).IsRepositoryAsync(Ct)).ShouldBeFalse();
        (await new GitRunner(Path.Combine(_root, "missing")).IsRepositoryAsync(Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task IsClean_DetectsModifiedAndUntracked()
    {
        (await _git.IsCleanAsync(Ct)).ShouldBeTrue();
        Write("new.txt", "x");
        (await _git.IsCleanAsync(Ct)).ShouldBeFalse();
        File.Delete(Path.Combine(Repo, "new.txt"));
        Write("README.md", "changed");
        (await _git.IsCleanAsync(Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task RepoRoot_And_CurrentBranch()
    {
        (await _git.GetRepoRootAsync(Ct)).ShouldBe(Path.GetFullPath(Repo));
        (await _git.CurrentBranchAsync(Ct)).ShouldBe("main");
    }

    [Fact]
    public async Task Commit_ReturnsShaAndMovesHead()
    {
        var before = await _git.HeadShaAsync(Ct);
        Write("a.txt", "a");
        await _git.AddAsync(["a.txt"], Ct);
        var sha = await _git.CommitAsync("add a", Ct);
        sha.Length.ShouldBe(40);
        sha.ShouldNotBe(before);
        (await _git.HeadShaAsync(Ct)).ShouldBe(sha);
        (await _git.CommitAsync("empty", Ct, allowEmpty: true)).ShouldNotBe(sha);
    }

    [Fact]
    public async Task Branch_CreateExistsCheckoutDelete()
    {
        (await _git.BranchExistsAsync("feature", Ct)).ShouldBeFalse();
        await _git.CreateBranchAsync("feature", "HEAD", Ct);
        (await _git.BranchExistsAsync("feature", Ct)).ShouldBeTrue();
        await _git.CheckoutAsync("feature", Ct);
        (await _git.CurrentBranchAsync(Ct)).ShouldBe("feature");
        await _git.CheckoutAsync("main", Ct);
        await _git.DeleteBranchAsync("feature", force: false, Ct);
        (await _git.BranchExistsAsync("feature", Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task WorktreeAdd_CreatesDirAndBranch_RunnerCommitsOnThatBranch()
    {
        var wtPath = Path.Combine(_root, "wt", "w1");
        var wt = await _git.WorktreeAddAsync(wtPath, "testgen/w1", "HEAD", Ct);

        Directory.Exists(wtPath).ShouldBeTrue();
        wt.WorkDir.ShouldBe(Path.GetFullPath(wtPath));
        (await _git.BranchExistsAsync("testgen/w1", Ct)).ShouldBeTrue();
        (await wt.CurrentBranchAsync(Ct)).ShouldBe("testgen/w1");

        Write("t.cs", "test", wtPath);
        await wt.AddAllAsync(Ct);
        var sha = await wt.CommitAsync("wt commit", Ct);

        (await _git.LogShasAsync("main..testgen/w1", Ct)).ShouldBe([sha]);
        (await _git.CurrentBranchAsync(Ct)).ShouldBe("main");

        var list = await _git.WorktreeListAsync(Ct);
        list.Count.ShouldBe(2);
        list.ShouldContain(w => w.Path == Path.GetFullPath(wtPath) && w.Branch == "testgen/w1" && w.Head == sha);
        list.ShouldContain(w => w.Path == Path.GetFullPath(Repo) && w.Branch == "main");
    }

    [Fact]
    public async Task WorktreeRemove_And_Prune()
    {
        var keep = Path.Combine(_root, "wt", "a");
        var gone = Path.Combine(_root, "wt", "b");
        var wt = await _git.WorktreeAddAsync(keep, "testgen/a", "HEAD", Ct);
        await _git.WorktreeAddAsync(gone, "testgen/b", "HEAD", Ct);
        Write("dirty.txt", "x", keep);

        await Should.ThrowAsync<GitException>(() => _git.WorktreeRemoveAsync(keep, force: false, Ct));
        await _git.WorktreeRemoveAsync(keep, force: true, Ct);
        Directory.Exists(keep).ShouldBeFalse();

        Directory.Delete(gone, true);
        await _git.WorktreePruneAsync(Ct);

        (await _git.WorktreeListAsync(Ct)).Count.ShouldBe(1);
        await _git.DeleteBranchAsync("testgen/a", force: true, Ct);
        (await _git.BranchExistsAsync("testgen/a", Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task Merge_WorktreeBranch_BringsFileIntoBase()
    {
        var wtPath = Path.Combine(_root, "wt", "m");
        var wt = await _git.WorktreeAddAsync(wtPath, "testgen/m", "HEAD", Ct);
        Write("merged.txt", "m", wtPath);
        await wt.AddAllAsync(Ct);
        await wt.CommitAsync("m", Ct);

        await _git.MergeAsync("testgen/m", Ct, "Merge testgen/m");

        File.Exists(Path.Combine(Repo, "merged.txt")).ShouldBeTrue();
        (await _git.RunAsync("log -1 --format=%s", Ct)).StdOut.Trim().ShouldBe("Merge testgen/m");
        (await _git.RunAsync("rev-list --parents -n 1 HEAD", Ct)).StdOut.Trim().Split(' ').Length.ShouldBe(3); // --no-ff
    }

    [Fact]
    public async Task Merge_Conflict_ThrowsAndAborts()
    {
        var wtPath = Path.Combine(_root, "wt", "c");
        var wt = await _git.WorktreeAddAsync(wtPath, "testgen/c", "HEAD", Ct);
        Write("README.md", "theirs", wtPath);
        await wt.AddAllAsync(Ct);
        await wt.CommitAsync("theirs", Ct);
        Write("README.md", "ours");
        await _git.AddAllAsync(Ct);
        var head = await _git.CommitAsync("ours", Ct);

        var ex = await Should.ThrowAsync<GitException>(() => _git.MergeAsync("testgen/c", Ct));

        ex.ExitCode.ShouldNotBe(0);
        (await _git.IsCleanAsync(Ct)).ShouldBeTrue();
        (await _git.HeadShaAsync(Ct)).ShouldBe(head);
        File.ReadAllText(Path.Combine(Repo, "README.md")).ShouldBe("ours");
    }

    [Fact]
    public async Task CherryPick_AppliesCommit_ConflictAborts()
    {
        await _git.CreateBranchAsync("side", "HEAD", Ct);
        await _git.CheckoutAsync("side", Ct);
        Write("picked.txt", "p");
        await _git.AddAllAsync(Ct);
        var pick = await _git.CommitAsync("pick me", Ct);
        Write("README.md", "side");
        await _git.AddAllAsync(Ct);
        var conflicting = await _git.CommitAsync("conflict", Ct);
        await _git.CheckoutAsync("main", Ct);

        await _git.CherryPickAsync(pick, Ct);
        File.Exists(Path.Combine(Repo, "picked.txt")).ShouldBeTrue();

        Write("README.md", "main");
        await _git.AddAllAsync(Ct);
        var head = await _git.CommitAsync("main change", Ct);
        await Should.ThrowAsync<GitException>(() => _git.CherryPickAsync(conflicting, Ct));
        (await _git.IsCleanAsync(Ct)).ShouldBeTrue();
        (await _git.HeadShaAsync(Ct)).ShouldBe(head);
    }

    [Fact]
    public async Task DiffNameOnly_ListsChangedFilesRelativeToRoot()
    {
        var from = await _git.HeadShaAsync(Ct);
        Directory.CreateDirectory(Path.Combine(Repo, "src", "sub"));
        Write(Path.Combine("src", "sub", "x.cs"), "x");
        Write("README.md", "changed");
        await _git.AddAllAsync(Ct);
        var to = await _git.CommitAsync("changes", Ct);

        var files = await new GitRunner(Path.Combine(Repo, "src")).DiffNameOnlyAsync(from, to, Ct);

        files.ShouldBe(["README.md", "src/sub/x.cs"], ignoreOrder: true);
    }

    [Fact]
    public async Task ResetHard_RestoresCommit()
    {
        var first = await _git.HeadShaAsync(Ct);
        Write("a.txt", "a");
        await _git.AddAllAsync(Ct);
        await _git.CommitAsync("a", Ct);

        await _git.ResetHardAsync(first, Ct);

        (await _git.HeadShaAsync(Ct)).ShouldBe(first);
        File.Exists(Path.Combine(Repo, "a.txt")).ShouldBeFalse();
    }

    [Fact]
    public async Task RunAsync_BadCommand_ThrowsGitExceptionWithExitCode()
    {
        var ex = await Should.ThrowAsync<GitException>(() => _git.RunAsync("no-such-command", Ct));
        ex.ExitCode.ShouldNotBe(0);
        ex.Args.ShouldBe("no-such-command");

        var r = await _git.RunAsync("rev-parse nope-ref", Ct, throwOnError: false);
        r.ExitCode.ShouldNotBe(0);
    }

    [Fact]
    public async Task RunAsync_Cancelled_Throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => _git.RunAsync("status", cts.Token));
    }

    [Fact]
    public async Task Commit_WithoutConfiguredUser_UsesFallbackIdentity()
    {
        await _git.RunAsync("config --unset user.name", Ct);
        await _git.RunAsync("config --unset user.email", Ct);
        // Hide global/system config so no identity exists anywhere. Tests in this class run sequentially.
        var oldGlobal = Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL");
        var oldNoSystem = Environment.GetEnvironmentVariable("GIT_CONFIG_NOSYSTEM");
        Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", "/dev/null");
        Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", "1");
        try
        {
            Write("b.txt", "b");
            await _git.AddAllAsync(Ct);
            await _git.CommitAsync("no user", Ct);
            (await _git.RunAsync("log -1 --format=%an<%ae>", Ct)).StdOut.Trim().ShouldBe("testgen<testgen@local>");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", oldGlobal);
            Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", oldNoSystem);
        }
    }

    [Fact]
    public async Task Commit_RespectsConfiguredUser()
    {
        Write("c.txt", "c");
        await _git.AddAllAsync(Ct);
        await _git.CommitAsync("with user", Ct);
        (await _git.RunAsync("log -1 --format=%an", Ct)).StdOut.Trim().ShouldBe("tester");
    }

    [Theory]
    [InlineData("Feature/Foo Bar", "feature-foo-bar")]
    [InlineData("  ..weird~^:?*[name]..  ", "weird-name")]
    [InlineData("MyClass.Method", "myclass.method")]
    [InlineData("a..b", "a.b")]
    [InlineData("x.lock", "x")]
    [InlineData("--abc--", "abc")]
    [InlineData("Zażółć gęślą", "za-g-l")]
    public void SanitizeBranchName_ProducesValidComponent(string input, string expected) =>
        GitRunner.SanitizeBranchName(input).ShouldBe(expected);

    [Fact]
    public void SanitizeBranchName_NothingUsable_Throws() =>
        Should.Throw<ArgumentException>(() => GitRunner.SanitizeBranchName("~~~"));
}
