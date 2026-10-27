using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Shouldly;
using TestGen.Cli;
using Xunit;

namespace TestGen.Tests.Cli.Orchestration;

public sealed class ProgramConfigTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("testgen-cfg-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    static IServiceProvider Services(string? solutionPath)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Llm:BaseUrl"] = "http://localhost:11434/v1/",
            ["Llm:Model"] = "m",
            ["TestGen:SolutionPath"] = solutionPath,
        });
        Program.ConfigureServices(builder.Services, builder.Configuration);
        return builder.Build().Services;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("/nope/App.sln")]
    public void Invalid_solution_path_fails_startup_validation(string? path)
    {
        var sp = Services(path);
        var ex = Should.Throw<OptionsValidationException>(() => sp.GetRequiredService<IStartupValidator>().Validate());
        ex.Message.ShouldContain("TestGen:SolutionPath");
    }

    [Fact]
    public void Out_of_range_setting_fails_validation()
    {
        var file = Path.Combine(_dir, "App.sln");
        File.WriteAllText(file, "");
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Llm:BaseUrl"] = "http://localhost:11434/v1/", ["Llm:Model"] = "m",
            ["TestGen:SolutionPath"] = file, ["TestGen:MinCoverage"] = "150",
        });
        Program.ConfigureServices(builder.Services, builder.Configuration);
        var ex = Should.Throw<OptionsValidationException>(() => builder.Build().Services.GetRequiredService<IStartupValidator>().Validate());
        ex.Message.ShouldContain("out of range");
    }

    [Fact]
    public void Non_solution_file_fails_validation()
    {
        var file = Path.Combine(_dir, "App.csproj");
        File.WriteAllText(file, "");
        var ex = Should.Throw<OptionsValidationException>(() => Services(file).GetRequiredService<IStartupValidator>().Validate());
        ex.Message.ShouldContain(".sln/.slnx");
    }

    [Fact]
    public void Valid_config_resolves_runner()
    {
        var file = Path.Combine(_dir, "App.slnx");
        File.WriteAllText(file, "");
        var sp = Services(file);
        sp.GetRequiredService<IStartupValidator>().Validate();
        sp.GetRequiredService<TestGenRunner>().ShouldNotBeNull();
    }
}
