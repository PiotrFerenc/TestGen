using System.Text.Json;
using System.Text.Json.Serialization;

namespace TestGen.Queue;

public sealed class RunState
{
    public string SolutionPath { get; set; } = "";
    public string BaseCommit { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public List<WorkItem> Items { get; set; } = [];

    public Dictionary<WorkStatus, int> Totals() =>
        Enum.GetValues<WorkStatus>().ToDictionary(s => s, s => Items.Count(i => i.Status == s));
}

public sealed class StateStore(string stateFilePath)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly SemaphoreSlim _gate = new(1, 1);

    public string FilePath => stateFilePath;

    public static string DefaultPath(string solutionDir) => Path.Combine(solutionDir, ".testgen", "state.json");

    public async Task<RunState?> LoadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!File.Exists(stateFilePath)) return null;
            await using var fs = File.OpenRead(stateFilePath);
            return await JsonSerializer.DeserializeAsync<RunState>(fs, Json, ct);
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(RunState state, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(stateFilePath))!);
            var tmp = stateFilePath + ".tmp";
            await using (var fs = File.Create(tmp))
                await JsonSerializer.SerializeAsync(fs, state, Json, ct);
            File.Move(tmp, stateFilePath, overwrite: true);
        }
        finally { _gate.Release(); }
    }
}
