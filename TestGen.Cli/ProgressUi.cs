using Spectre.Console;
using TestGen.Queue;

namespace TestGen.Cli;

/// <summary>Prints one line per work-item status change with a running done/failed/skipped/total counter.</summary>
public sealed class ProgressUi : IDisposable
{
    readonly WorkerPool _pool;
    readonly IAnsiConsole _console;
    readonly Lock _gate = new();
    readonly Dictionary<string, WorkStatus> _status = [];
    int _total;

    public ProgressUi(WorkerPool pool, RunState state, IAnsiConsole? console = null)
    {
        _pool = pool;
        _console = console ?? AnsiConsole.Console;
        _total = state.Items.Count;
        foreach (var i in state.Items) _status[i.Id] = i.Status;
        pool.ItemChanged += OnChanged;
    }

    void OnChanged(WorkItem item)
    {
        lock (_gate)
        {
            if (_status.TryAdd(item.Id, item.Status)) _total++;
            else _status[item.Id] = item.Status;
            var colour = item.Status switch
            {
                WorkStatus.Done => "green",
                WorkStatus.Failed => "red",
                WorkStatus.Skipped => "yellow",
                WorkStatus.Running => "blue",
                _ => "grey",
            };
            int Count(WorkStatus s) => _status.Values.Count(v => v == s);
            _console.MarkupLine($"[grey]{Markup.Escape(item.Id)}[/] [{colour}]{item.Status}[/] {(string.IsNullOrEmpty(item.Reason) ? "" : Markup.Escape(item.Reason) + " ")}" +
                                $"[grey]({Count(WorkStatus.Done)}/{Count(WorkStatus.Failed)}/{Count(WorkStatus.Skipped)}/{_total})[/]");
        }
    }

    public static void Section(string title) => AnsiConsole.Write(new Rule($"[bold]{Markup.Escape(title)}[/]").LeftJustified());

    public void Dispose() => _pool.ItemChanged -= OnChanged;
}
