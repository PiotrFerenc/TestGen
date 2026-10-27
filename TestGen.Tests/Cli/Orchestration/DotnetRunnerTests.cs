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

    [Fact]
    public void ErrorLines_keep_the_failed_test_message_with_expected_and_actual_but_not_the_stack()
    {
        const string output = """
            [xUnit.net 00:00:00.25]     Sample.GreeterTests.Greet [FAIL]
              Failed Sample.GreeterTests.Greet [35 ms]
              Error Message:
               Shouldly.ShouldAssertException : new Greeter().Greet("Ann")
                should be
            "Hello, Bob!"
                but was
            "Hello, Ann!"
              Stack Trace:
                 at Sample.GreeterTests.Greet() in /x/GreeterTests.cs:line 11

            Failed!  - Failed:     1, Passed:     6
            """;

        var lines = new DotnetResult(1, output).ErrorLines;

        lines.ShouldContain("\"Hello, Bob!\"");
        lines.ShouldContain("\"Hello, Ann!\"");
        lines.ShouldContain(l => l.Contains("but was"));
        lines.ShouldNotContain(l => l.Contains("line 11"));
    }

    [Fact]
    public void ErrorLines_deduplicate_repeated_compiler_errors()
    {
        const string e = "/x/A.cs(10,5): error CS0103: The name 'Foo' does not exist";
        new DotnetResult(1, e + "\n" + e + "\n").ErrorLines.ShouldBe([e]);
    }
}
