using NSubstitute;
using Shouldly;
using Spectre.Console;
using TestGen.Cli;
using TestGen.Queue;
using Xunit;

namespace TestGen.Tests.Cli.Orchestration;

public sealed class ProgressUiTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("testgen-ui-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task Concurrent_item_changes_print_one_line_each()
    {
        var items = Enumerable.Range(0, 50).Select(i => new WorkItem { Id = $"item-{i}", TargetTestFile = $"/t/{i}.cs" }).ToList();
        var state = new RunState { Items = items };
        var processor = Substitute.For<IWorkItemProcessor>();
        processor.ProcessAsync(default!, default).ReturnsForAnyArgs(async _ => { await Task.Yield(); return new WorkResult(WorkStatus.Done); });
        var pool = new WorkerPool(processor, new StateStore(Path.Combine(_dir, "state.json")));
        var output = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(output), Ansi = AnsiSupport.No });

        using (new ProgressUi(pool, state, console))
            await pool.RunAsync(state, 8, CancellationToken.None);

        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Length.ShouldBe(100); // Running + Done per item
        lines.ShouldContain(l => l.Contains("(50/0/0/50)"));
    }
}
