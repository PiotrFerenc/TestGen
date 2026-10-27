using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using TestGen.Analysis;
using TestGen.Git;

namespace TestGen.Cli.Bootstrap;

public sealed record BootstrapResult(IReadOnlyList<string> CreatedProjects, string? CommitSha);

/// <summary>Creates <c>&lt;Name&gt;.Tests</c> projects for production projects that have none, before the queue starts.</summary>
public sealed class TestProjectBootstrapper(IGitRunner repoGit, IDotnetRunner dotnet, TestGenOptions options, ILogger<TestProjectBootstrapper> logger)
{
    static readonly string[] TestSuffixes = [".Tests", ".UnitTests", ".Test"];
    static readonly string[] Libraries = ["NSubstitute", "Moq", "FakeItEasy", "Shouldly", "FluentAssertions"];

    static readonly Dictionary<string, string> DefaultVersions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft.NET.Test.Sdk"] = "17.14.1",
        ["xunit"] = "2.9.3",
        ["xunit.runner.visualstudio"] = "3.1.4",
        ["NUnit"] = "4.3.2",
        ["NUnit3TestAdapter"] = "5.0.0",
        ["MSTest.TestFramework"] = "3.8.3",
        ["MSTest.TestAdapter"] = "3.8.3",
        ["NSubstitute"] = "6.2.0",
        ["Moq"] = "4.20.72",
        ["FakeItEasy"] = "8.3.0",
        ["Shouldly"] = "4.3.0",
        ["FluentAssertions"] = "7.2.0",
        ["AutoFixture"] = "4.18.1",
    };

    public async Task<BootstrapResult> BootstrapAsync(Solution solution, TestConventions conventions, CancellationToken ct)
    {
        var tests = SolutionLoader.TestProjects(solution).ToList();
        var missing = SolutionLoader.ProductionProjects(solution)
            .Where(p => p.FilePath is not null && !tests.Any(t =>
                t.ProjectReferences.Any(r => r.ProjectId == p.Id)
                || TestSuffixes.Any(s => string.Equals(t.Name, p.Name + s, StringComparison.OrdinalIgnoreCase))))
            .ToList();
        if (missing.Count == 0) return new BootstrapResult([], null);

        var testsRoot = CommonTestsDir(tests);
        var versions = ExistingVersions(tests);
        var (packages, usings) = Stack(conventions);
        var created = new List<string>();

        foreach (var prod in missing)
        {
            var prodPath = prod.FilePath!;
            var name = prod.Name + ".Tests";
            var dir = Path.Combine(testsRoot ?? Path.GetDirectoryName(Path.GetDirectoryName(prodPath))!, name);
            var csproj = Path.Combine(dir, name + ".csproj");
            if (File.Exists(csproj))
            {
                logger.LogWarning("{Csproj} already exists but is not in the solution; leaving it alone", csproj);
                continue;
            }

            Directory.CreateDirectory(dir);
            File.WriteAllText(csproj, BuildCsproj(TargetFramework(prodPath), Path.GetRelativePath(dir, prodPath).Replace('\\', '/'),
                packages.Select(p => (p, versions.GetValueOrDefault(p) ?? DefaultVersions[p]))));
            File.WriteAllText(Path.Combine(dir, "GlobalUsings.cs"), string.Concat(usings.Select(u => $"global using {u};\n")));

            Ensure(await dotnet.RunAsync($"sln \"{options.SolutionPath}\" add \"{csproj}\"", options.SolutionDir, TimeSpan.FromMinutes(2), ct), "dotnet sln add", csproj);
            Ensure(await dotnet.RestoreAsync(csproj, ct), "dotnet restore", csproj);
            logger.LogInformation("Created test project {Csproj} for {Project}", csproj, prod.Name);
            created.Add(csproj);
        }

        if (created.Count == 0) return new BootstrapResult([], null);
        await repoGit.AddAllAsync(ct);
        var sha = await repoGit.CommitAsync("testgen: bootstrap test projects", ct);
        return new BootstrapResult(created, sha);
    }

    static void Ensure(DotnetResult result, string what, string csproj)
    {
        if (result.Success) return;
        var tail = string.Join('\n', result.Output.Split('\n').TakeLast(20));
        throw new InvalidOperationException($"{what} failed for {csproj} (exit {result.ExitCode}):\n{tail}");
    }

    /// <summary>The single <c>test</c>/<c>tests</c> ancestor shared by every existing test project, if any.</summary>
    static string? CommonTestsDir(List<Project> tests)
    {
        var roots = tests.Select(t => TestsAncestor(t.FilePath)).Distinct().ToList();
        return roots is [{ } root] ? root : null;
    }

    static string? TestsAncestor(string? path)
    {
        for (var dir = path is null ? null : new FileInfo(path).Directory; dir is not null; dir = dir.Parent)
            if (dir.Name.Equals("test", StringComparison.OrdinalIgnoreCase) || dir.Name.Equals("tests", StringComparison.OrdinalIgnoreCase))
                return dir.FullName;
        return null;
    }

    static Dictionary<string, string> ExistingVersions(List<Project> tests)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in tests.Select(t => t.FilePath).Where(File.Exists))
        {
            try
            {
                foreach (var pr in XDocument.Load(path!).Descendants().Where(e => e.Name.LocalName == "PackageReference"))
                    if ((string?)pr.Attribute("Include") is { } id && ((string?)pr.Attribute("Version") ?? pr.Elements().FirstOrDefault(e => e.Name.LocalName == "Version")?.Value) is { Length: > 0 } v)
                        result.TryAdd(id, v);
            }
            catch (System.Xml.XmlException) { /* unreadable csproj: fall back to defaults */ }
        }
        return result;
    }

    static string TargetFramework(string csproj)
    {
        try
        {
            var props = XDocument.Load(csproj).Descendants().ToList();
            var single = props.FirstOrDefault(e => e.Name.LocalName == "TargetFramework")?.Value.Trim();
            if (!string.IsNullOrEmpty(single)) return single;
            var multi = props.FirstOrDefault(e => e.Name.LocalName == "TargetFrameworks")?.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            if (!string.IsNullOrEmpty(multi)) return multi;
        }
        catch (Exception e) when (e is System.Xml.XmlException or IOException) { }
        return "net10.0"; // ponytail: TargetFramework set only in Directory.Build.props is not resolved
    }

    static (List<string> Packages, List<string> Usings) Stack(TestConventions c)
    {
        var packages = new List<string> { "Microsoft.NET.Test.Sdk" };
        var usings = new List<string>();
        switch (c.Framework.ToLowerInvariant())
        {
            case "nunit": packages.AddRange(["NUnit", "NUnit3TestAdapter"]); usings.Add("NUnit.Framework"); break;
            case "mstest": packages.AddRange(["MSTest.TestFramework", "MSTest.TestAdapter"]); usings.Add("Microsoft.VisualStudio.TestTools.UnitTesting"); break;
            default: packages.AddRange(["xunit", "xunit.runner.visualstudio"]); usings.Add("Xunit"); break;
        }
        // Mocking/assertion packages share their root namespace with the package id.
        foreach (var lib in new[] { c.Mocking, c.Assertions })
            if (Libraries.FirstOrDefault(k => k.Equals(lib, StringComparison.OrdinalIgnoreCase)) is { } pkg)
            {
                packages.Add(pkg);
                usings.Add(pkg);
            }
        if (c.UsesAutoFixture)
        {
            packages.Add("AutoFixture");
            usings.Add("AutoFixture");
        }
        return (packages, usings);
    }

    static string BuildCsproj(string tfm, string prodRef, IEnumerable<(string Id, string Version)> packages) =>
        new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"),
            new XElement("PropertyGroup",
                new XElement("TargetFramework", tfm),
                new XElement("Nullable", "enable"),
                new XElement("ImplicitUsings", "enable"),
                new XElement("IsPackable", "false"),
                new XElement("IsTestProject", "true")),
            new XElement("ItemGroup", packages.Select(p => new XElement("PackageReference", new XAttribute("Include", p.Id), new XAttribute("Version", p.Version)))),
            new XElement("ItemGroup", new XElement("ProjectReference", new XAttribute("Include", prodRef))))
        .ToString() + "\n";
}
