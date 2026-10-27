using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using TestGen.Cli;
using TestGen.Coverage;
using TestGen.Git;
using TestGen.Queue;
using Xunit;

namespace TestGen.Tests.Cli.Orchestration;

public sealed class PreflightTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("testgen-preflight-").FullName;
    readonly IGitRunner _git = Substitute.For<IGitRunner>();
    readonly IDotnetRunner _dotnet = Substitute.For<IDotnetRunner>();
    readonly ICoverageRunner _coverage = Substitute.For<ICoverageRunner>();
    readonly TestGenOptions _options;

    public PreflightTests()
    {
        _options = new TestGenOptions { SolutionPath = Path.Combine(_dir, "App.sln") };
        File.WriteAllText(_options.SolutionPath, "");
        _git.IsRepositoryAsync(default).ReturnsForAnyArgs(true);
        _git.IsCleanAsync(default).ReturnsForAnyArgs(true);
        _git.GetRepoRootAsync(default).ReturnsForAnyArgs(_dir);
        _coverage.IsToolAvailableAsync(default).ReturnsForAnyArgs(true);
        _dotnet.BuildAsync(default!, default).ReturnsForAnyArgs(new DotnetResult(0, ""));
        _dotnet.TestAsync(default!, default, default).ReturnsForAnyArgs(new DotnetResult(0, ""));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    Task<string> Run() => new Preflight(_git, _dotnet, _coverage, _options, NullLogger<Preflight>.Instance).RunAsync(CancellationToken.None);

    async Task<string> Fails() => (await Should.ThrowAsync<PreflightException>(Run)).Message;

    [Fact]
    public async Task Happy_path_returns_repo_root_and_runs_baseline()
    {
        (await Run()).ShouldBe(_dir);
        await _dotnet.Received(1).BuildAsync(_options.SolutionPath, Arg.Any<CancellationToken>());
        await _dotnet.Received(1).TestAsync(_options.SolutionPath, _options.TestTimeout, Arg.Any<CancellationToken>(), null, true);
    }

    [Fact]
    public async Task Missing_solution_fails()
    {
        File.Delete(_options.SolutionPath);
        (await Fails()).ShouldContain("Solution file not found");
    }

    [Fact]
    public async Task Not_a_repo_fails()
    {
        _git.IsRepositoryAsync(default).ReturnsForAnyArgs(false);
        (await Fails()).ShouldContain("not inside a git repository");
    }

    [Fact]
    public async Task Dirty_tree_fails()
    {
        _git.IsCleanAsync(default).ReturnsForAnyArgs(false);
        _git.CurrentBranchAsync(default).ReturnsForAnyArgs(TestGenOptions.ResultBranch); // but no state.json
        (await Fails()).ShouldContain("uncommitted changes");
    }

    [Fact]
    public async Task Dirty_tree_on_other_branch_with_state_fails()
    {
        await new StateStore(StateStore.DefaultPath(_dir)).SaveAsync(new RunState());
        _git.IsCleanAsync(default).ReturnsForAnyArgs(false);
        _git.CurrentBranchAsync(default).ReturnsForAnyArgs("main");
        (await Fails()).ShouldContain("uncommitted changes");
    }

    [Fact]
    public async Task Dirty_tree_allowed_when_resuming_on_result_branch()
    {
        await new StateStore(StateStore.DefaultPath(_dir)).SaveAsync(new RunState());
        _git.IsCleanAsync(default).ReturnsForAnyArgs(false);
        _git.CurrentBranchAsync(default).ReturnsForAnyArgs(TestGenOptions.ResultBranch);
        (await Run()).ShouldBe(_dir);
    }

    [Fact]
    public async Task Missing_coverage_tool_fails_with_install_hint()
    {
        _coverage.IsToolAvailableAsync(default).ReturnsForAnyArgs(false);
        (await Fails()).ShouldContain("dotnet tool install -g dotnet-coverage");
    }

    [Fact]
    public async Task Red_baseline_build_fails_with_first_20_errors()
    {
        var output = string.Join('\n', Enumerable.Range(1, 30).Select(i => $"Foo.cs(1,1): error CS{i:0000}: bad"));
        _dotnet.BuildAsync(default!, default).ReturnsForAnyArgs(new DotnetResult(1, output));
        var msg = await Fails();
        msg.ShouldStartWith("Baseline build failed");
        msg.ShouldContain("CS0020");
        msg.ShouldNotContain("CS0021");
        await _dotnet.DidNotReceiveWithAnyArgs().TestAsync(default!, default, default);
    }

    [Fact]
    public async Task Red_baseline_tests_fail()
    {
        _dotnet.TestAsync(default!, default, default).ReturnsForAnyArgs(new DotnetResult(1, "[FAIL] FooTests.Bar"));
        (await Fails()).ShouldBe("Baseline tests failed:\n[FAIL] FooTests.Bar");
    }
}
