using Shouldly;
using TestGen.Coverage;
using Xunit;

namespace TestGen.Tests.Coverage;

public class CoverageRunnerTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("testgen-runner-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    // Fake dotnet-coverage: shell script (Linux/macOS only).
    string FakeTool(string body)
    {
        var path = Path.Combine(_dir, "fake-tool.sh");
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    [Fact]
    public async Task IsToolAvailable_ReturnsBoolWithoutThrowing()
    {
        var real = await new CoverageRunner().IsToolAvailableAsync(CancellationToken.None);
        (real is true or false).ShouldBeTrue();
        (await new CoverageRunner(toolPath: "/nonexistent/dotnet-coverage").IsToolAvailableAsync(CancellationToken.None)).ShouldBeFalse();
        (await new CoverageRunner(toolPath: FakeTool("exit 0")).IsToolAvailableAsync(CancellationToken.None)).ShouldBeTrue();
    }

    [Fact]
    public async Task Collect_MissingTool_Throws()
    {
        var runner = new CoverageRunner(toolPath: "/nonexistent/dotnet-coverage");
        await Should.ThrowAsync<CoverageException>(() =>
            runner.CollectAsync(Path.Combine(_dir, "x.sln"), Path.Combine(_dir, "c.xml"), TimeSpan.FromSeconds(10), CancellationToken.None));
    }

    [Fact]
    public async Task Collect_NonZeroExit_ThrowsWithOutputTail()
    {
        var runner = new CoverageRunner(toolPath: FakeTool("echo boom-output; exit 3"));
        var ex = await Should.ThrowAsync<CoverageException>(() =>
            runner.CollectAsync(Path.Combine(_dir, "x.sln"), Path.Combine(_dir, "c.xml"), TimeSpan.FromSeconds(10), CancellationToken.None));
        ex.Message.ShouldContain("boom-output");
        ex.Message.ShouldContain("3");
    }

    [Fact]
    public async Task Collect_NoOutputFile_Throws()
    {
        var runner = new CoverageRunner(toolPath: FakeTool("exit 0"));
        var ex = await Should.ThrowAsync<CoverageException>(() =>
            runner.CollectAsync(Path.Combine(_dir, "x.sln"), Path.Combine(_dir, "c.xml"), TimeSpan.FromSeconds(10), CancellationToken.None));
        ex.Message.ShouldContain("not created");
    }

    [Fact]
    public async Task Collect_Timeout_Throws()
    {
        var runner = new CoverageRunner(toolPath: FakeTool("sleep 10"));
        var ex = await Should.ThrowAsync<CoverageException>(() =>
            runner.CollectAsync(Path.Combine(_dir, "x.sln"), Path.Combine(_dir, "c.xml"), TimeSpan.FromMilliseconds(300), CancellationToken.None));
        ex.Message.ShouldContain("timed out");
    }

    [Fact]
    public async Task Collect_PassesArgumentsAndReturnsOutputPath()
    {
        // Writes "$@" (one arg per line) into the file following -o.
        var tool = FakeTool("""out=""; prev=""; for a in "$@"; do [ "$prev" = "-o" ] && out="$a"; prev="$a"; done; printf '%s\n' "$@" > "$out"; pwd >> "$out" """);
        var sln = Path.Combine(_dir, "My.sln");
        var result = await new CoverageRunner(toolPath: tool).CollectAsync(sln, Path.Combine(_dir, "out", "c.xml"),
            TimeSpan.FromSeconds(10), CancellationToken.None, extraTestArgs: "--filter X", noBuild: true);

        result.ShouldBe(Path.Combine(_dir, "out", "c.xml"));
        var lines = File.ReadAllLines(result);
        lines[0].ShouldBe("collect");
        lines[1].ShouldBe($"dotnet test \"{sln}\" --no-build --filter X");
        lines[2..5].ShouldBe(["-f", "cobertura", "-o"]);
        lines[^1].ShouldBe(_dir);
    }
}
