using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TestGen.Cli;
using TestGen.Git;
using Xunit;

namespace TestGen.Tests.Cli.Orchestration;

public sealed class ResultBranchTests
{
    readonly IGitRunner _git = Substitute.For<IGitRunner>();
    static readonly DateTime Now = new(2026, 10, 27, 13, 5, 9);

    Task Prepare(bool hasState) => TestGenRunner.PrepareResultBranchAsync(_git, hasState, NullLogger.Instance, Now, CancellationToken.None);

    [Fact]
    public async Task Stale_branch_without_state_is_renamed_not_deleted()
    {
        _git.CurrentBranchAsync(default).ReturnsForAnyArgs("main");
        _git.BranchExistsAsync(TestGenOptions.ResultBranch, Arg.Any<CancellationToken>()).Returns(true, false);

        await Prepare(hasState: false);

        await _git.Received(1).RunAsync("branch -m testgen/result testgen/result-20261027-130509", Arg.Any<CancellationToken>());
        await _git.DidNotReceiveWithAnyArgs().DeleteBranchAsync(default!, default, default);
        await _git.Received(1).CreateBranchAsync(TestGenOptions.ResultBranch, "HEAD", Arg.Any<CancellationToken>());
        await _git.Received(1).CheckoutAsync(TestGenOptions.ResultBranch, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Checked_out_result_branch_is_reused()
    {
        _git.CurrentBranchAsync(default).ReturnsForAnyArgs(TestGenOptions.ResultBranch);
        _git.BranchExistsAsync(default!, default).ReturnsForAnyArgs(true);

        await Prepare(hasState: false);

        await _git.DidNotReceiveWithAnyArgs().RunAsync(default!, default);
        await _git.DidNotReceiveWithAnyArgs().CreateBranchAsync(default!, default!, default);
        await _git.DidNotReceiveWithAnyArgs().CheckoutAsync(default!, default);
    }
}
