using System.Runtime.CompilerServices;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Logging;

namespace TestGen.Analysis;

public static class SolutionLoader
{
    static readonly Lock Gate = new();
    static readonly string[] TestSuffixes = [".Tests", ".Test", ".UnitTests", ".Specs"];
    static readonly string[] TestAssemblies = ["xunit", "nunit.framework", "Microsoft.VisualStudio.TestPlatform.TestFramework"];

    public static void EnsureMSBuildRegistered()
    {
        lock (Gate)
            if (!MSBuildLocator.IsRegistered) MSBuildLocator.RegisterDefaults();
    }

    public static async Task<Solution> OpenAsync(string solutionPath, ILogger? logger, CancellationToken ct)
    {
        EnsureMSBuildRegistered();
        var solution = await OpenCoreAsync(solutionPath, logger, ct);
        if (!solution.Projects.Any()) throw new InvalidOperationException($"No projects loaded from {solutionPath}");
        logger?.LogInformation("Loaded {Count} projects from {Path}", solution.Projects.Count(), solutionPath);
        return solution;
    }

    // Kept separate so MSBuild-related types are not touched before the locator registers.
    [MethodImpl(MethodImplOptions.NoInlining)]
    static Task<Solution> OpenCoreAsync(string solutionPath, ILogger? logger, CancellationToken ct)
    {
        var workspace = MSBuildWorkspace.Create();
        workspace.RegisterWorkspaceFailedHandler(e => logger?.LogWarning("Workspace {Kind}: {Message}", e.Diagnostic.Kind, e.Diagnostic.Message));
        return workspace.OpenSolutionAsync(solutionPath, cancellationToken: ct);
    }

    public static bool IsTestProject(Project project) =>
        TestSuffixes.Any(s => project.Name.EndsWith(s, StringComparison.OrdinalIgnoreCase))
        || project.MetadataReferences.OfType<PortableExecutableReference>()
            .Select(r => Path.GetFileName(r.FilePath ?? ""))
            .Any(f => TestAssemblies.Any(a => f.StartsWith(a, StringComparison.OrdinalIgnoreCase)));

    public static IEnumerable<Project> TestProjects(Solution solution) => solution.Projects.Where(IsTestProject);

    public static IEnumerable<Project> ProductionProjects(Solution solution) => solution.Projects.Where(p => !IsTestProject(p));
}
