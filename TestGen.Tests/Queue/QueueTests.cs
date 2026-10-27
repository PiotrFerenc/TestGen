using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using TestGen.Queue;
using Xunit;

namespace TestGen.Tests.Queue;

public sealed class QueueTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("testgen-queue-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static WorkItem Item(string name, int lines, WorkStatus status = WorkStatus.Pending, int attempts = 0)
    {
        var file = $"/repo/tests/{name}Tests.cs";
        return new WorkItem
        {
            Id = WorkItem.MakeId(file),
            TargetTestFile = file,
            TestProjectPath = "/repo/tests/Tests.csproj",
            Classes = [new WorkClass($"App.{name}", $"/repo/src/{name}.cs", lines, ["M1"])],
            UncoveredLines = lines,
            Status = status,
            Attempts = attempts,
        };
    }

    private StateStore Store() => new(StateStore.DefaultPath(_dir));

    [Fact]
    public void MakeId_is_stable_unique_and_sanitized()
    {
        var a = WorkItem.MakeId("/repo/tests/Foo Bar.Tests.cs");
        a.ShouldBe(WorkItem.MakeId("/repo/tests/Foo Bar.Tests.cs"));
        a.ShouldMatch("^[a-z0-9]+(-[a-z0-9]+)*$");
        a.ShouldStartWith("foo-bar-tests-");
        WorkItem.MakeId("/repo/other/Foo Bar.Tests.cs").ShouldNotBe(a);
    }

    [Fact]
    public async Task StateStore_round_trips_and_leaves_no_tmp()
    {
        var store = Store();
        var state = WorkPlanner.Plan("/repo/a.sln", "abc123", [Item("Foo", 5, WorkStatus.Done)]);
        state.Items[0].CoverageBefore = 0.5;

        await store.SaveAsync(state, default);
        var loaded = await store.LoadAsync(default);

        loaded.ShouldNotBeNull();
        loaded.BaseCommit.ShouldBe("abc123");
        loaded.Items.Single().Status.ShouldBe(WorkStatus.Done);
        loaded.Items[0].Classes.Single().UncoveredMethods.ShouldBe(["M1"]);
        loaded.Items[0].CoverageBefore.ShouldBe(0.5);
        File.ReadAllText(store.FilePath).ShouldContain("\"Done\"");
        File.Exists(store.FilePath + ".tmp").ShouldBeFalse();
        StateStore.DefaultPath(_dir).ShouldBe(Path.Combine(_dir, ".testgen", "state.json"));
    }

    [Fact]
    public async Task StateStore_missing_file_returns_null() =>
        (await Store().LoadAsync(default)).ShouldBeNull();

    [Fact]
    public void Plan_orders_by_uncovered_lines_desc()
    {
        var state = WorkPlanner.Plan("s", "c", [Item("A", 1), Item("B", 10), Item("C", 5)]);
        state.Items.Select(i => i.UncoveredLines).ShouldBe([10, 5, 1]);
        state.Totals()[WorkStatus.Pending].ShouldBe(3);
        state.Totals()[WorkStatus.Done].ShouldBe(0);
    }

    [Fact]
    public void Resume_applies_rules()
    {
        var previous = WorkPlanner.Plan("s", "c",
        [
            Item("Done", 1, WorkStatus.Done, 1),
            Item("Running", 2, WorkStatus.Running, 1),
            Item("Exhausted", 3, WorkStatus.Failed, 3),
            Item("Retry", 4, WorkStatus.Failed, 1),
            Item("Gone", 5),
        ]);

        var resumed = WorkPlanner.Resume(previous,
            [Item("Done", 1), Item("Running", 2), Item("Exhausted", 3), Item("Retry", 4), Item("New", 6)], maxRetries: 3);

        var byName = resumed.Items.ToDictionary(i => i.Classes[0].FullName[4..]);
        byName.Keys.ShouldBe(["New", "Retry", "Exhausted", "Running", "Done"]);
        byName["Done"].Status.ShouldBe(WorkStatus.Done);
        byName["Running"].Status.ShouldBe(WorkStatus.Pending);
        byName["Running"].Attempts.ShouldBe(1);
        byName["Exhausted"].Status.ShouldBe(WorkStatus.Failed);
        byName["Retry"].Status.ShouldBe(WorkStatus.Pending);
        byName["New"].Status.ShouldBe(WorkStatus.Pending);
        resumed.BaseCommit.ShouldBe("c");
    }

    [Fact]
    public void Resume_carries_tokens_branch_and_coverage()
    {
        var old = Item("Retry", 4, WorkStatus.Failed, 1);
        old.PromptTokens = 100;
        old.CompletionTokens = 40;
        old.Branch = "testgen/retry";
        old.CoverageBefore = 0.1;
        old.CoverageAfter = 0.5;

        var item = WorkPlanner.Resume(WorkPlanner.Plan("s", "c", [old]), [Item("Retry", 4)], maxRetries: 3).Items.Single();

        item.Status.ShouldBe(WorkStatus.Pending);
        item.Attempts.ShouldBe(1);
        item.PromptTokens.ShouldBe(100);
        item.CompletionTokens.ShouldBe(40);
        item.Branch.ShouldBe("testgen/retry");
        item.CoverageBefore.ShouldBe(0.1);
        item.CoverageAfter.ShouldBe(0.5);
    }

    [Fact]
    public async Task WorkerPool_store_failure_surfaces_instead_of_hanging()
    {
        // Parent of the state file is a regular file -> every SaveAsync throws IOException.
        var blocker = Path.Combine(_dir, "blocker");
        File.WriteAllText(blocker, "");
        var store = new StateStore(Path.Combine(blocker, "state.json"));
        var processor = Substitute.For<IWorkItemProcessor>();
        processor.ProcessAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>()).Returns(new WorkResult(WorkStatus.Done));
        var items = Enumerable.Range(0, 10).Select(i => Item($"I{i}", i)).ToList();

        var run = new WorkerPool(processor, store).RunAsync(WorkPlanner.Plan("s", "c", items), 2, default);

        await Should.ThrowAsync<IOException>(() => run.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task WorkerPool_applies_results_and_exceptions()
    {
        var items = new[] { Item("Ok", 3), Item("Bad", 2), Item("Skip", 1), Item("Boom", 0), Item("Old", 9, WorkStatus.Done) };
        var processor = Substitute.For<IWorkItemProcessor>();
        processor.ProcessAsync(items[0], Arg.Any<CancellationToken>()).Returns(new WorkResult(WorkStatus.Done, Branch: "testgen/ok", PromptTokens: 10, CompletionTokens: 5, CoverageAfter: 0.9));
        processor.ProcessAsync(items[1], Arg.Any<CancellationToken>()).Returns(new WorkResult(WorkStatus.Failed, "red"));
        processor.ProcessAsync(items[2], Arg.Any<CancellationToken>()).Returns(new WorkResult(WorkStatus.Skipped, "db"));
        processor.ProcessAsync(items[3], Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("kaput"));
        var store = Store();
        var pool = new WorkerPool(processor, store);
        var changes = new List<(string, WorkStatus)>();
        pool.ItemChanged += i => { lock (changes) changes.Add((i.Id, i.Status)); };

        var state = await pool.RunAsync(WorkPlanner.Plan("s", "c", items), 2, default);

        items[0].Status.ShouldBe(WorkStatus.Done);
        items[0].Branch.ShouldBe("testgen/ok");
        items[0].PromptTokens.ShouldBe(10);
        items[0].CoverageAfter.ShouldBe(0.9);
        items[1].Status.ShouldBe(WorkStatus.Failed);
        items[1].Reason.ShouldBe("red");
        items[2].Status.ShouldBe(WorkStatus.Skipped);
        items[3].Status.ShouldBe(WorkStatus.Failed);
        items[3].Reason.ShouldBe("kaput");
        items.Take(4).ShouldAllBe(i => i.Attempts == 1);
        await processor.DidNotReceive().ProcessAsync(items[4], Arg.Any<CancellationToken>());
        changes.Count.ShouldBe(8);
        changes.ShouldContain((items[0].Id, WorkStatus.Running));

        var saved = await store.LoadAsync(default);
        saved!.Totals()[WorkStatus.Failed].ShouldBe(2);
        saved.Totals()[WorkStatus.Done].ShouldBe(2);
        state.Items.Count.ShouldBe(5);
    }

    [Fact]
    public async Task WorkerPool_saves_running_state_before_processing()
    {
        var store = Store();
        var processor = Substitute.For<IWorkItemProcessor>();
        WorkStatus? seen = null;
        processor.ProcessAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            seen = (await store.LoadAsync(default))!.Items[0].Status;
            return new WorkResult(WorkStatus.Done);
        });

        await new WorkerPool(processor, store).RunAsync(WorkPlanner.Plan("s", "c", [Item("A", 1)]), 1, default);

        seen.ShouldBe(WorkStatus.Running);
        (await store.LoadAsync(default))!.Items[0].Status.ShouldBe(WorkStatus.Done);
    }

    [Fact]
    public async Task WorkerPool_runs_items_concurrently()
    {
        using var barrier = new Barrier(2);
        var processor = Substitute.For<IWorkItemProcessor>();
        processor.ProcessAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>()).Returns(_ => Task.Run(() =>
            barrier.SignalAndWait(TimeSpan.FromSeconds(5))
                ? new WorkResult(WorkStatus.Done)
                : new WorkResult(WorkStatus.Failed, "not concurrent")));

        var state = await new WorkerPool(processor, Store()).RunAsync(WorkPlanner.Plan("s", "c", [Item("A", 1), Item("B", 2)]), 2, default);

        state.Items.ShouldAllBe(i => i.Status == WorkStatus.Done);
    }

    [Fact]
    public async Task WorkerPool_cancellation_resets_running_to_pending()
    {
        using var cts = new CancellationTokenSource();
        var started = new TaskCompletionSource();
        var processor = Substitute.For<IWorkItemProcessor>();
        processor.ProcessAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>()).Returns(async ci =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ci.Arg<CancellationToken>());
            return new WorkResult(WorkStatus.Done);
        });
        var store = Store();
        var run = new WorkerPool(processor, store).RunAsync(WorkPlanner.Plan("s", "c", [Item("A", 2), Item("B", 1)]), 1, cts.Token);

        await started.Task;
        cts.Cancel();
        var state = await run;

        state.Items.ShouldAllBe(i => i.Status == WorkStatus.Pending);
        state.Items[0].Attempts.ShouldBe(1);
        (await store.LoadAsync(default))!.Items.ShouldAllBe(i => i.Status == WorkStatus.Pending);
    }

    [Fact]
    public void DefaultParallelism_is_between_1_and_4() =>
        WorkerPool.DefaultParallelism.ShouldBeInRange(1, 4);
}
