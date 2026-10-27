using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using TestGen.Cli;
using TestGen.Cli.Merge;
using TestGen.Git;
using TestGen.Queue;
using Xunit;

namespace TestGen.Tests.Cli.Merge;

public sealed class ResultMergerTests
{
    static readonly DotnetResult Ok = new(0, "");
    readonly IGitRunner _git = Substitute.For<IGitRunner>();
    readonly IDotnetRunner _dotnet = Substitute.For<IDotnetRunner>();
    readonly TestGenOptions _options = new() { SolutionPath = "/repo/All.sln" };

    public ResultMergerTests()
    {
        _git.CurrentBranchAsync(default).ReturnsForAnyArgs(TestGenOptions.ResultBranch);
        _git.HeadShaAsync(default).ReturnsForAnyArgs("sha0", "sha1", "sha2");
        _dotnet.BuildAsync(default!, default).ReturnsForAnyArgs(Ok);
        _dotnet.TestAsync(default!, default, default).ReturnsForAnyArgs(Ok);
    }

    ResultMerger Sut() => new(_git, _dotnet, _options, NullLogger<ResultMerger>.Instance);

    static RunState State(params WorkItem[] items) => new() { Items = [.. items] };

    static WorkItem Done(string id) => new() { Id = id, Status = WorkStatus.Done, Branch = "testgen/" + id };

    [Fact]
    public async Task Merges_done_items_in_order_and_deletes_branches()
    {
        var skipped = new WorkItem { Id = "c", Status = WorkStatus.Failed, Branch = "testgen/c" };
        var state = State(Done("a"), skipped, Done("b"));

        var report = await Sut().MergeAsync(state, CancellationToken.None);

        report.MergedIds.ShouldBe(["a", "b"]);
        report.Rejected.ShouldBeEmpty();
        report.FullBuildOk.ShouldBeTrue();
        report.FullTestOk.ShouldBeTrue();
        Received.InOrder(() =>
        {
            _git.MergeAsync("testgen/a", Arg.Any<CancellationToken>(), "testgen: merge a");
            _git.DeleteBranchAsync("testgen/a", true, Arg.Any<CancellationToken>());
            _git.MergeAsync("testgen/b", Arg.Any<CancellationToken>(), "testgen: merge b");
            _git.DeleteBranchAsync("testgen/b", true, Arg.Any<CancellationToken>());
        });
        await _git.DidNotReceive().MergeAsync("testgen/c", Arg.Any<CancellationToken>(), Arg.Any<string?>());
        await _dotnet.Received(2).TestAsync("/repo/All.sln", _options.TestTimeout, Arg.Any<CancellationToken>(), null, true);
    }

    [Fact]
    public async Task Second_merge_of_same_state_is_a_no_op()
    {
        var state = State(Done("a"));
        await Sut().MergeAsync(state, CancellationToken.None);

        var again = await Sut().MergeAsync(state, CancellationToken.None);

        again.MergedIds.ShouldBeEmpty();
        again.Rejected.ShouldBeEmpty();
        state.Items[0].Status.ShouldBe(WorkStatus.Done);
        await _git.Received(1).MergeAsync("testgen/a", Arg.Any<CancellationToken>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task Conflict_rejects_item_and_marks_it_failed()
    {
        var a = Done("a");
        _git.MergeAsync("testgen/a", Arg.Any<CancellationToken>(), Arg.Any<string?>()).ThrowsAsync(new GitException("merge", 1, "CONFLICT"));

        var report = await Sut().MergeAsync(State(a, Done("b")), CancellationToken.None);

        report.Rejected.ShouldBe([("a", "merge conflict")]);
        report.MergedIds.ShouldBe(["b"]);
        a.Status.ShouldBe(WorkStatus.Failed);
        a.Reason.ShouldBe("merge conflict");
        await _git.DidNotReceive().DeleteBranchAsync("testgen/a", Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Red_tests_after_merge_reset_to_previous_head_and_reject()
    {
        var a = Done("a");
        _dotnet.TestAsync(default!, default, default).ReturnsForAnyArgs(new DotnetResult(1, "Failed Acme.Tests.Foo [12 ms]\n"), Ok);

        var report = await Sut().MergeAsync(State(a), CancellationToken.None);

        await _git.Received(1).ResetHardAsync("sha0", Arg.Any<CancellationToken>());
        report.MergedIds.ShouldBeEmpty();
        report.Rejected.Single().Reason.ShouldBe("solution build/test failed after merge: Failed Acme.Tests.Foo [12 ms]");
        a.Status.ShouldBe(WorkStatus.Failed);
        // Final verification of the reset HEAD.
        await _dotnet.Received(2).BuildAsync("/repo/All.sln", Arg.Any<CancellationToken>());
        report.FullBuildOk.ShouldBeTrue();
        report.FullTestOk.ShouldBeTrue();
    }

    [Fact]
    public async Task Nothing_merged_still_runs_final_build_and_reports_it()
    {
        _dotnet.BuildAsync(default!, default).ReturnsForAnyArgs(new DotnetResult(1, "x.cs(1,1): error CS1002: ; expected"));

        var report = await Sut().MergeAsync(State(), CancellationToken.None);

        report.FullBuildOk.ShouldBeFalse();
        report.FullTestOk.ShouldBeFalse();
        await _dotnet.DidNotReceiveWithAnyArgs().TestAsync(default!, default, default);
    }

    [Fact]
    public async Task Wrong_branch_throws()
    {
        _git.CurrentBranchAsync(default).ReturnsForAnyArgs("main");

        await Should.ThrowAsync<InvalidOperationException>(() => Sut().MergeAsync(State(Done("a")), CancellationToken.None));
        await _git.DidNotReceiveWithAnyArgs().MergeAsync(default!, default);
    }
}
