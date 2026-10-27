using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace TestGen.Queue;

public sealed class WorkerPool(IWorkItemProcessor processor, StateStore store, ILogger<WorkerPool>? logger = null)
{
    public static int DefaultParallelism => Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    /// <summary>Fired after every status change of an item (from worker threads).</summary>
    public event Action<WorkItem>? ItemChanged;

    /// <summary>Processes all Pending items. Cancellation returns normally with interrupted items reset to Pending.</summary>
    public async Task<RunState> RunAsync(RunState state, int parallelism, CancellationToken ct)
    {
        parallelism = Math.Max(1, parallelism);
        var channel = Channel.CreateBounded<WorkItem>(parallelism);
        // Cancelled by ct or by the first faulting worker, so the producer never blocks on a channel nobody reads.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var producer = Task.Run(async () =>
        {
            try
            {
                foreach (var item in state.Items.Where(i => i.Status == WorkStatus.Pending).OrderByDescending(i => i.UncoveredLines))
                    await channel.Writer.WriteAsync(item, stop.Token);
            }
            catch (OperationCanceledException) { }
            finally { channel.Writer.Complete(); }
        }, CancellationToken.None);

        var workers = Enumerable.Range(0, parallelism).Select(_ => Task.Run(async () =>
        {
            try
            {
                await foreach (var item in channel.Reader.ReadAllAsync(stop.Token))
                    await ProcessOne(state, item, ct);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch
            {
                stop.Cancel();
                throw;
            }
        }, CancellationToken.None)).ToList();

        await Task.WhenAll(workers.Append(producer)); // rethrows a worker's exception

        if (ct.IsCancellationRequested)
        {
            foreach (var item in state.Items.Where(i => i.Status == WorkStatus.Running))
                Change(item, WorkStatus.Pending, item.Reason);
            await store.SaveAsync(state, CancellationToken.None);
        }
        return state;
    }

    private async Task ProcessOne(RunState state, WorkItem item, CancellationToken ct)
    {
        item.Attempts++;
        Change(item, WorkStatus.Running, null);
        await store.SaveAsync(state, CancellationToken.None);

        try
        {
            var r = await processor.ProcessAsync(item, ct);
            item.Branch = r.Branch ?? item.Branch;
            item.PromptTokens += r.PromptTokens;
            item.CompletionTokens += r.CompletionTokens;
            item.CoverageAfter = r.CoverageAfter ?? item.CoverageAfter;
            Change(item, r.Status, r.Reason);
            logger?.LogInformation("{Id}: {Status} {Reason}", item.Id, r.Status, r.Reason ?? "");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            item.Attempts--; // a Ctrl+C is not a failed attempt; otherwise repeated cancels let Resume mark the item Failed
            Change(item, WorkStatus.Pending, null);
            await store.SaveAsync(state, CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            Change(item, WorkStatus.Failed, ex.Message);
            logger?.LogError(ex, "{Id}: processor failed", item.Id);
        }
        await store.SaveAsync(state, CancellationToken.None);
    }

    private void Change(WorkItem item, WorkStatus status, string? reason)
    {
        item.Status = status;
        item.Reason = reason;
        ItemChanged?.Invoke(item);
    }
}
