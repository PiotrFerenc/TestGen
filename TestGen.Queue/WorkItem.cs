using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace TestGen.Queue;

public enum WorkStatus { Pending, Running, Done, Failed, Skipped }

public sealed record WorkClass(string FullName, string SourceFile, int UncoveredLines, IReadOnlyList<string> UncoveredMethods);

public sealed record WorkResult(
    WorkStatus Status,
    string? Reason = null,
    string? Branch = null,
    int PromptTokens = 0,
    int CompletionTokens = 0,
    double? CoverageAfter = null);

public interface IWorkItemProcessor
{
    Task<WorkResult> ProcessAsync(WorkItem item, CancellationToken ct);
}

public sealed partial class WorkItem
{
    public string Id { get; set; } = "";
    public string TargetTestFile { get; set; } = "";
    public string TestProjectPath { get; set; } = "";
    public List<WorkClass> Classes { get; set; } = [];
    public int UncoveredLines { get; set; }
    public WorkStatus Status { get; set; }
    public int Attempts { get; set; }
    public string? Reason { get; set; }
    public string? Branch { get; set; }
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public double? CoverageBefore { get; set; }
    public double? CoverageAfter { get; set; }

    /// <summary>Slug of the file name plus an 8-char hash of the full path; safe as a git branch component.</summary>
    public static string MakeId(string targetTestFile)
    {
        var name = NonSlug().Replace(Path.GetFileNameWithoutExtension(targetTestFile).ToLowerInvariant(), "-").Trim('-');
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(targetTestFile)))[..8];
        return name.Length == 0 ? hash : $"{name}-{hash}";
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlug();
}
