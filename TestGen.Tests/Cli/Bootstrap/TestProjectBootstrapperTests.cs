using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using TestGen.Analysis;
using TestGen.Cli;
using TestGen.Cli.Bootstrap;
using TestGen.Git;
using Xunit;

namespace TestGen.Tests.Cli.Bootstrap;

public sealed class TestProjectBootstrapperTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "testgen-bootstrap-" + Guid.NewGuid().ToString("N"));
    readonly IGitRunner _git = Substitute.For<IGitRunner>();
    readonly IDotnetRunner _dotnet = Substitute.For<IDotnetRunner>();
    readonly TestGenOptions _options;
    Solution _solution = new AdhocWorkspace().CurrentSolution;

    public TestProjectBootstrapperTests()
    {
        Directory.CreateDirectory(_root);
        _options = new TestGenOptions { SolutionPath = Path.Combine(_root, "All.sln") };
        _dotnet.RunAsync(default!, default!, default, default).ReturnsForAnyArgs(new DotnetResult(0, ""));
        _dotnet.RestoreAsync(default!, default).ReturnsForAnyArgs(new DotnetResult(0, ""));
        _git.CommitAsync(default!, default).ReturnsForAnyArgs("abc123");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    TestProjectBootstrapper Sut() => new(_git, _dotnet, _options, NullLogger<TestProjectBootstrapper>.Instance);

    ProjectId AddProject(string relativeCsproj, string csprojXml, params ProjectId[] refs)
    {
        var path = Path.Combine(_root, relativeCsproj);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, csprojXml);
        var id = ProjectId.CreateNewId();
        var name = Path.GetFileNameWithoutExtension(path);
        _solution = _solution.AddProject(ProjectInfo.Create(id, VersionStamp.Default, name, name, LanguageNames.CSharp, filePath: path,
            projectReferences: refs.Select(r => new ProjectReference(r))));
        return id;
    }

    static string Csproj(string tfm = "net8.0", string packages = "") =>
        $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>{tfm}</TargetFramework></PropertyGroup><ItemGroup>{packages}</ItemGroup></Project>";

    static Dictionary<string, string> Packages(XDocument doc) => doc.Descendants("PackageReference")
        .ToDictionary(e => (string)e.Attribute("Include")!, e => (string)e.Attribute("Version")!);

    [Fact]
    public async Task Creates_sibling_xunit_project_registers_restores_and_commits()
    {
        AddProject("src/Acme.Core/Acme.Core.csproj", Csproj());

        var result = await Sut().BootstrapAsync(_solution, TestConventions.Default("xunit", "NSubstitute", "Shouldly"), CancellationToken.None);

        var csproj = Path.Combine(_root, "src/Acme.Core.Tests/Acme.Core.Tests.csproj");
        result.CreatedProjects.ShouldBe([csproj]);
        result.CommitSha.ShouldBe("abc123");
        var doc = XDocument.Load(csproj);
        doc.Descendants("TargetFramework").Single().Value.ShouldBe("net8.0");
        doc.Descendants("IsTestProject").Single().Value.ShouldBe("true");
        doc.Descendants("ProjectReference").Single().Attribute("Include")!.Value.ShouldBe("../Acme.Core/Acme.Core.csproj");
        Packages(doc).ShouldBe(new Dictionary<string, string>
        {
            ["Microsoft.NET.Test.Sdk"] = "17.14.1", ["xunit"] = "2.9.3", ["xunit.runner.visualstudio"] = "3.1.4",
            ["NSubstitute"] = "6.2.0", ["Shouldly"] = "4.3.0",
        }, ignoreOrder: true);
        File.ReadAllText(Path.Combine(_root, "src/Acme.Core.Tests/GlobalUsings.cs"))
            .ShouldBe("global using Xunit;\nglobal using NSubstitute;\nglobal using Shouldly;\n");

        Received.InOrder(() =>
        {
            _dotnet.RestoreAsync(csproj, Arg.Any<CancellationToken>());
            _dotnet.RunAsync($"sln \"{_options.SolutionPath}\" add \"{csproj}\"", _options.SolutionDir, TimeSpan.FromMinutes(2), Arg.Any<CancellationToken>());
        });
        await _git.Received(1).AddAsync(Arg.Is<IEnumerable<string>>(p => p.SequenceEqual(new[] { _options.SolutionPath, Path.GetDirectoryName(csproj)! })), Arg.Any<CancellationToken>());
        await _git.DidNotReceive().AddAllAsync(Arg.Any<CancellationToken>());
        await _git.Received(1).CommitAsync("testgen: bootstrap test projects", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Nunit_moq_fluentassertions_stack_with_autofixture()
    {
        AddProject("src/Acme.Core/Acme.Core.csproj", Csproj("net9.0;net8.0").Replace("TargetFramework>", "TargetFrameworks>"));
        var conventions = TestConventions.Default("nunit", "Moq", "FluentAssertions") with { UsesAutoFixture = true };

        var result = await Sut().BootstrapAsync(_solution, conventions, CancellationToken.None);

        var doc = XDocument.Load(result.CreatedProjects.Single());
        doc.Descendants("TargetFramework").Single().Value.ShouldBe("net9.0");
        Packages(doc).ShouldBe(new Dictionary<string, string>
        {
            ["Microsoft.NET.Test.Sdk"] = "17.14.1", ["NUnit"] = "4.3.2", ["NUnit3TestAdapter"] = "5.0.0",
            ["Moq"] = "4.20.72", ["FluentAssertions"] = "7.2.0", ["AutoFixture"] = "4.18.1",
        }, ignoreOrder: true);
        File.ReadAllText(Path.Combine(Path.GetDirectoryName(result.CreatedProjects[0])!, "GlobalUsings.cs"))
            .ShouldContain("global using NUnit.Framework;");
    }

    [Fact]
    public async Task Honours_tests_dir_and_copies_existing_versions()
    {
        AddProject("src/Acme.Core/Acme.Core.csproj", Csproj());
        var other = AddProject("src/Other/Other.csproj", Csproj());
        AddProject("tests/Other.Tests/Other.Tests.csproj", Csproj("net8.0", "<PackageReference Include=\"NSubstitute\" Version=\"5.1.0\" /><PackageReference Include=\"xunit\"><Version>2.4.2</Version></PackageReference>"), other);

        var result = await Sut().BootstrapAsync(_solution, TestConventions.Default("xunit", "NSubstitute", "Shouldly"), CancellationToken.None);

        var csproj = Path.Combine(_root, "tests/Acme.Core.Tests/Acme.Core.Tests.csproj");
        result.CreatedProjects.ShouldBe([csproj]);
        var doc = XDocument.Load(csproj);
        doc.Descendants("ProjectReference").Single().Attribute("Include")!.Value.ShouldBe("../../src/Acme.Core/Acme.Core.csproj");
        var packages = Packages(doc);
        packages["NSubstitute"].ShouldBe("5.1.0");
        packages["xunit"].ShouldBe("2.4.2");
        packages["Shouldly"].ShouldBe("4.3.0");
    }

    [Fact]
    public async Task Failing_sln_add_throws_and_does_not_commit()
    {
        AddProject("src/Acme.Core/Acme.Core.csproj", Csproj());
        _dotnet.RunAsync(default!, default!, default, default).ReturnsForAnyArgs(new DotnetResult(1, "boom: solution is broken"));

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => Sut().BootstrapAsync(_solution, TestConventions.Default("xunit", "NSubstitute", "Shouldly"), CancellationToken.None));

        ex.Message.ShouldContain("boom: solution is broken");
        await _git.DidNotReceiveWithAnyArgs().CommitAsync(default!, default);
    }

    [Fact]
    public async Task Failing_restore_does_not_touch_the_solution()
    {
        AddProject("src/Acme.Core/Acme.Core.csproj", Csproj());
        _dotnet.RestoreAsync(default!, default).ReturnsForAnyArgs(new DotnetResult(1, "NU1101: package not found"));

        await Should.ThrowAsync<InvalidOperationException>(() => Sut().BootstrapAsync(_solution, TestConventions.Default("xunit", "NSubstitute", "Shouldly"), CancellationToken.None));

        await _dotnet.DidNotReceiveWithAnyArgs().RunAsync(default!, default!, default, default);
        await _git.DidNotReceiveWithAnyArgs().CommitAsync(default!, default);
    }

    [Fact]
    public async Task Nothing_to_do_returns_empty_result_without_commit()
    {
        var core = AddProject("src/Acme.Core/Acme.Core.csproj", Csproj());
        AddProject("src/Lib/Lib.csproj", Csproj());
        AddProject("tests/Acme.Core.Tests/Acme.Core.Tests.csproj", Csproj(), core);
        AddProject("tests/Lib.UnitTests/Lib.UnitTests.csproj", Csproj()); // matched by name only

        var result = await Sut().BootstrapAsync(_solution, TestConventions.Default("xunit", "NSubstitute", "Shouldly"), CancellationToken.None);

        result.CreatedProjects.ShouldBeEmpty();
        result.CommitSha.ShouldBeNull();
        await _git.DidNotReceiveWithAnyArgs().CommitAsync(default!, default);
        await _dotnet.DidNotReceiveWithAnyArgs().RestoreAsync(default!, default);
    }
}
