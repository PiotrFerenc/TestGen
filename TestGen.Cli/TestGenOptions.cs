using System.ComponentModel.DataAnnotations;

namespace TestGen.Cli;

/// <summary>Bound from the <c>TestGen</c> section of appsettings.json.</summary>
public sealed class TestGenOptions
{
    public const string SectionName = "TestGen";
    public const string ResultBranch = "testgen/result";
    public const string BranchPrefix = "testgen/";

    [Required(ErrorMessage = "TestGen:SolutionPath is required (absolute path to a .sln/.slnx).")]
    public string SolutionPath { get; set; } = "";

    /// <summary>0 = <c>WorkerPool.DefaultParallelism</c>.</summary>
    [Range(0, 64)]
    public int Parallel { get; set; }

    [Range(0, 20)]
    public int MaxRetries { get; set; } = 3;

    /// <summary>Classes at or above this line-coverage percentage are not queued.</summary>
    [Range(0, 100)]
    public int MinCoverage { get; set; } = 80;

    [Range(1, int.MaxValue)]
    public int TestTimeoutSeconds { get; set; } = 600;

    /// <summary>Run new tests twice to catch flaky ones.</summary>
    public bool RunNewTestsTwice { get; set; }

    public DefaultStackOptions DefaultStack { get; set; } = new();

    public string SolutionDir => Path.GetDirectoryName(Path.GetFullPath(SolutionPath))!;

    /// <summary>Scratch directory: <c>&lt;SolutionDir&gt;/.testgen</c>.</summary>
    public string WorkDir => Path.Combine(SolutionDir, ".testgen");

    public TimeSpan TestTimeout => TimeSpan.FromSeconds(TestTimeoutSeconds);
}

public sealed class DefaultStackOptions
{
    public string Framework { get; set; } = "xunit";
    public string Mocking { get; set; } = "NSubstitute";
    public string Assertions { get; set; } = "Shouldly";
}
