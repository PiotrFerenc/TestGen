using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using TestGen.Analysis;
using TestGen.Cli;
using TestGen.Cli.Worker;
using TestGen.Coverage;
using TestGen.Git;
using TestGen.Llm;
using TestGen.Queue;
using Xunit;

namespace TestGen.Tests.Cli.Worker;

public sealed class WorkItemProcessorTests : IDisposable
{
    const string CalcSource = """
        namespace Acme.Core;

        public interface IClock { int Now { get; } }

        public class Calc(IClock clock)
        {
            public int Add(int a, int b) => a + b;
            public int Twice(int x)
            {
                return x * 2;
            }
        }

        public class FooDbContext { }

        public class Repo(FooDbContext db)
        {
            public int Count() { return 1; }
        }
        """;

    const string ExistingTests = """
        using Xunit;

        namespace Acme.Core.Tests;

        public class CalcTests
        {
            [Fact]
            public void Add_Works()
            {
                Assert.Equal(3, new Calc(null!).Add(1, 2));
            }
        }
        """;

    const string Placeholder = "namespace Acme.Core.Tests;\n\npublic class Other { }\n";

    const string ValidSnippet = """
        Here you go:
        ```csharp
        using NSubstitute;
        using Shouldly;

        [Fact]
        public void Twice_Doubles()
        {
            new Calc(Substitute.For<IClock>()).Twice(2).ShouldBe(4);
        }
        ```
        """;

    const string NoAssertSnippet = """
        ```csharp
        [Fact]
        public void Twice_Runs()
        {
            new Calc(null!).Twice(2);
        }
        ```
        """;

    static readonly MetadataReference[] Framework = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator)
        .Where(p => Path.GetFileName(p) is var f && (f.StartsWith("System.") || f is "System.dll" or "mscorlib.dll" or "netstandard.dll"))
        .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
        .ToArray();

    readonly string _root = Path.Combine(Path.GetTempPath(), "tg-worker-" + Guid.NewGuid().ToString("N")[..8]);
    readonly IGitRunner _git = Substitute.For<IGitRunner>();
    readonly IGitRunner _wt = Substitute.For<IGitRunner>();
    readonly ILlmClient _llm = Substitute.For<ILlmClient>();
    readonly IDotnetRunner _dotnet = Substitute.For<IDotnetRunner>();
    readonly ICoverageRunner _coverage = Substitute.For<ICoverageRunner>();
    readonly TestGenOptions _options;

    string TestFile => $"{_root}/tests/Acme.Core.Tests/CalcTests.cs";
    string TestCsproj => $"{_root}/tests/Acme.Core.Tests/Acme.Core.Tests.csproj";
    string Id => WorkItem.MakeId(TestFile);
    string WtDir => Path.Combine(_options.WorkDir, "wt", Id);
    string WtTestFile => $"{WtDir}/tests/Acme.Core.Tests/CalcTests.cs";
    string Branch => "testgen/" + Id;

    public WorkItemProcessorTests()
    {
        _options = new TestGenOptions { SolutionPath = $"{_root}/Acme.slnx", MaxRetries = 2 };
        _git.GetRepoRootAsync(default).ReturnsForAnyArgs(_root);
        _git.WorktreeAddAsync(default!, default!, default!, default).ReturnsForAnyArgs(ci =>
        {
            var dir = ci.ArgAt<string>(0);
            foreach (var sub in new[] { "src", "tests" }) CopyDir(Path.Combine(_root, sub), Path.Combine(dir, sub));
            return _wt;
        });
        _wt.WorkDir.Returns(_ => WtDir);
        _wt.RunAsync("status --porcelain", Arg.Any<CancellationToken>(), Arg.Any<bool>())
            .Returns(new GitResult(0, " M tests/Acme.Core.Tests/CalcTests.cs\n?? tests/Acme.Core.Tests/obj/x.json\n", ""));
        _dotnet.BuildAsync(default!, default).ReturnsForAnyArgs(new DotnetResult(0, ""));
        _dotnet.TestAsync(default!, default, default).ReturnsForAnyArgs(new DotnetResult(0, ""));
        CoverageAfter();
        _llm.CompleteAsync(default!, default).ReturnsForAnyArgs(Response(ValidSnippet));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    static LlmResponse Response(string content) => new(content, 10, 5, "stop");

    static string Cobertura(int twiceHits) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <coverage line-rate="0.5" version="1.9" timestamp="1700000000">
          <packages><package name="Acme.Core"><classes>
            <class name="Acme.Core.Calc" filename="/src/Acme.Core/Calc.cs" line-rate="0.5">
              <methods>
                <method name="Add" signature="(int, int)"><lines><line number="7" hits="1" branch="false" /></lines></method>
                <method name="Twice" signature="(int)"><lines><line number="10" hits="{twiceHits}" branch="false" /></lines></method>
              </methods>
            </class>
            <class name="Acme.Core.Repo" filename="/src/Acme.Core/Calc.cs" line-rate="0">
              <methods>
                <method name="Count" signature="()"><lines><line number="19" hits="0" branch="false" /></lines></method>
              </methods>
            </class>
          </classes></package></packages>
        </coverage>
        """;

    int _twiceHitsAfter = 1;

    void CoverageAfter() =>
        _coverage.CollectAsync(default!, default!, default, default).ReturnsForAnyArgs(ci =>
        {
            var path = ci.ArgAt<string>(1);
            File.WriteAllText(path, Cobertura(_twiceHitsAfter));
            return path;
        });

    WorkItemProcessor Processor(bool existingTestFile = true)
    {
        var tests = existingTestFile ? new[] { ("CalcTests.cs", ExistingTests) } : new[] { ("Other.cs", Placeholder) };
        var solution = new AdhocWorkspace().CurrentSolution;
        var prodId = ProjectId.CreateNewId();
        var testId = ProjectId.CreateNewId();
        solution = AddProject(solution, prodId, "Acme.Core", $"{_root}/src/Acme.Core", Framework, [("Calc.cs", CalcSource)]);
        solution = AddProject(solution, testId, "Acme.Core.Tests", $"{_root}/tests/Acme.Core.Tests", [.. Framework, .. Analysis.RoslynTestWorkspace.TestLibraries], tests);
        solution = solution.AddProjectReference(testId, new ProjectReference(prodId));

        return new WorkItemProcessor(solution, TestConventions.Default("xunit", "NSubstitute", "Shouldly"),
            CoberturaParser.Parse(Cobertura(twiceHits: 0)), _git, _llm,
            new PromptBuilder(Options.Create(new LlmOptions { BaseUrl = "http://localhost/", Model = "m" })),
            _coverage, _dotnet, _options, NullLogger<WorkItemProcessor>.Instance);
    }

    static Solution AddProject(Solution solution, ProjectId id, string name, string dir, IEnumerable<MetadataReference> refs, (string File, string Code)[] files)
    {
        solution = solution.AddProject(ProjectInfo.Create(id, VersionStamp.Default, name, name, LanguageNames.CSharp,
                filePath: $"{dir}/{name}.csproj",
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable),
                parseOptions: new CSharpParseOptions(LanguageVersion.Latest),
                metadataReferences: refs)
            .WithDefaultNamespace(name));
        Directory.CreateDirectory(dir);
        foreach (var (file, code) in files)
        {
            File.WriteAllText(Path.Combine(dir, file), code);
            solution = solution.AddDocument(DocumentId.CreateNewId(id), file, SourceText.From(code), filePath: $"{dir}/{file}");
        }
        return solution;
    }

    static void CopyDir(string from, string to)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    WorkItem Item(string cls = "Acme.Core.Calc") => new()
    {
        Id = Id,
        TargetTestFile = TestFile,
        TestProjectPath = TestCsproj,
        Classes = [new WorkClass(cls, $"{_root}/src/Acme.Core/Calc.cs", 1, ["Twice"])],
        CoverageBefore = 0.5,
    };

    string LastPromptOfCall(int index) =>
        ((IReadOnlyList<ChatMessage>)_llm.ReceivedCalls().ElementAt(index).GetArguments()[0]!)[^1].Content;

    [Fact]
    public async Task HappyPath_CommitsTestInWorktreeAndReturnsDone()
    {
        var result = await Processor().ProcessAsync(Item(), CancellationToken.None);

        result.Status.ShouldBe(WorkStatus.Done, result.Reason);
        result.Branch.ShouldBe(Branch);
        result.CoverageAfter.ShouldBe(1.0);
        var text = await File.ReadAllTextAsync(WtTestFile);
        text.ShouldContain("Twice_Doubles");
        text.ShouldContain("Add_Works");
        text.ShouldContain("using Shouldly;");
        await _git.Received().WorktreeAddAsync(WtDir, Branch, TestGenOptions.ResultBranch, Arg.Any<CancellationToken>());
        await _wt.Received().AddAsync(Arg.Is<IEnumerable<string>>(p => p.SequenceEqual(new[] { "tests/Acme.Core.Tests/CalcTests.cs" })), Arg.Any<CancellationToken>());
        await _wt.Received().CommitAsync(Arg.Is<string>(m => m.Contains("Acme.Core.Calc")), Arg.Any<CancellationToken>());
        await _git.Received().WorktreeRemoveAsync(WtDir, true, Arg.Any<CancellationToken>());
        await _git.DidNotReceiveWithAnyArgs().DeleteBranchAsync(default!, default, default);
        File.Exists(Path.Combine(WtDir, ".testgen-coverage.xml")).ShouldBeFalse();
        // Main tree untouched.
        (await File.ReadAllTextAsync(TestFile)).ShouldBe(ExistingTests);
    }

    [Fact]
    public async Task NoExistingTestFile_CreatesNewFile()
    {
        var result = await Processor(existingTestFile: false).ProcessAsync(Item(), CancellationToken.None);

        result.Status.ShouldBe(WorkStatus.Done, result.Reason);
        var text = await File.ReadAllTextAsync(WtTestFile);
        text.ShouldContain("namespace Acme.Core.Tests;");
        text.ShouldContain("public class CalcTests");
        text.ShouldContain("Twice_Doubles");
        LastPromptOfCall(0).ShouldContain("No test file exists yet; target class `CalcTests`");
    }

    [Fact]
    public async Task InvalidSnippetThenValid_RetriesAndSumsTokens()
    {
        _llm.CompleteAsync(default!, default).ReturnsForAnyArgs(Response(NoAssertSnippet), Response(ValidSnippet));

        var result = await Processor().ProcessAsync(Item(), CancellationToken.None);

        result.Status.ShouldBe(WorkStatus.Done, result.Reason);
        _llm.ReceivedCalls().Count().ShouldBe(2);
        LastPromptOfCall(1).ShouldContain("does not compile or its tests fail");
        result.PromptTokens.ShouldBe(20);
        result.CompletionTokens.ShouldBe(10);
    }

    [Fact]
    public async Task BuildFailureThenSuccess_RepairPromptContainsErrorLine()
    {
        const string error = "CalcTests.cs(12,5): error CS0103: The name 'x' does not exist";
        _dotnet.BuildAsync(default!, default).ReturnsForAnyArgs(new DotnetResult(1, "Build FAILED\n" + error), new DotnetResult(0, ""));

        var result = await Processor().ProcessAsync(Item(), CancellationToken.None);

        result.Status.ShouldBe(WorkStatus.Done, result.Reason);
        _llm.ReceivedCalls().Count().ShouldBe(2);
        LastPromptOfCall(1).ShouldContain(error);
        // Re-inserted from the original text, so the test exists once.
        (await File.ReadAllTextAsync(WtTestFile)).Split("Twice_Doubles").Length.ShouldBe(2);
    }

    [Fact]
    public async Task RetriesExhausted_FailsAndRollsBack()
    {
        _llm.CompleteAsync(default!, default).ReturnsForAnyArgs(Response(NoAssertSnippet));

        var result = await Processor().ProcessAsync(Item(), CancellationToken.None);

        result.Status.ShouldBe(WorkStatus.Failed);
        result.Reason!.ShouldContain("retries exhausted");
        _llm.ReceivedCalls().Count().ShouldBe(_options.MaxRetries + 1);
        await _git.Received().DeleteBranchAsync(Branch, true, Arg.Any<CancellationToken>());
        await _git.Received().WorktreeRemoveAsync(WtDir, true, Arg.Any<CancellationToken>());
        await _wt.DidNotReceiveWithAnyArgs().CommitAsync(default!, default);
    }

    [Fact]
    public async Task NoCoverageGain_Fails()
    {
        _twiceHitsAfter = 0;

        var result = await Processor().ProcessAsync(Item(), CancellationToken.None);

        result.Status.ShouldBe(WorkStatus.Failed);
        result.Reason.ShouldBe("no coverage gain");
        await _git.Received().DeleteBranchAsync(Branch, true, Arg.Any<CancellationToken>());
        await _wt.DidNotReceiveWithAnyArgs().CommitAsync(default!, default);
    }

    [Fact]
    public async Task ProductionFileChanged_Fails()
    {
        _wt.RunAsync("status --porcelain", Arg.Any<CancellationToken>(), Arg.Any<bool>())
            .Returns(new GitResult(0, " M tests/Acme.Core.Tests/CalcTests.cs\n M src/Acme.Core/Calc.cs\n", ""));

        var result = await Processor().ProcessAsync(Item(), CancellationToken.None);

        result.Status.ShouldBe(WorkStatus.Failed);
        result.Reason!.ShouldContain("src/Acme.Core/Calc.cs");
    }

    [Fact]
    public async Task UnmockableDependency_Skipped()
    {
        var result = await Processor().ProcessAsync(Item("Acme.Core.Repo"), CancellationToken.None);

        result.Status.ShouldBe(WorkStatus.Skipped);
        result.Reason!.ShouldContain("unmockable dependency: Acme.Core.FooDbContext");
        await _llm.DidNotReceiveWithAnyArgs().CompleteAsync(default!, default);
        await _git.Received().DeleteBranchAsync(Branch, true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LlmThrows_FailsWithReason()
    {
        _llm.CompleteAsync(default!, default).ThrowsAsyncForAnyArgs(new LlmException("model offline"));

        var result = await Processor().ProcessAsync(Item(), CancellationToken.None);

        result.Status.ShouldBe(WorkStatus.Failed);
        result.Reason.ShouldBe("model offline");
        await _git.Received().DeleteBranchAsync(Branch, true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LeftoversFromPreviousRun_AreCleanedFirst()
    {
        Directory.CreateDirectory(WtDir);
        _git.BranchExistsAsync(Branch, Arg.Any<CancellationToken>()).Returns(true);

        var result = await Processor().ProcessAsync(Item(), CancellationToken.None);

        result.Status.ShouldBe(WorkStatus.Done, result.Reason);
        _git.ReceivedCalls().Select(c => c.GetMethodInfo().Name)
            .Where(n => n is "WorktreeRemoveAsync" or "DeleteBranchAsync" or "WorktreeAddAsync").Take(3)
            .ShouldBe(["WorktreeRemoveAsync", "DeleteBranchAsync", "WorktreeAddAsync"]);
    }
}
