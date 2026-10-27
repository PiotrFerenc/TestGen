using Shouldly;
using TestGen.Cli;
using Xunit;

namespace TestGen.Tests.Cli.Orchestration;

public sealed class DotnetRunnerTests
{
    [Fact]
    public async Task Drain_with_a_stream_left_open_returns_what_completed_after_the_timeout()
    {
        var timedOut = false;
        var open = new TaskCompletionSource<string>().Task; // a grandchild keeps stderr open

        var output = await DotnetRunner.DrainAsync(Task.FromResult("built ok\n"), open, TimeSpan.FromMilliseconds(50), CancellationToken.None, () => timedOut = true);

        output.ShouldBe("built ok\n");
        timedOut.ShouldBeTrue();
    }
}
