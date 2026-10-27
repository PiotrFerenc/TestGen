using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Spectre.Console;
using TestGen.Cli;
using TestGen.Coverage;
using TestGen.Git;
using TestGen.Llm;

// Content root = binary dir: appsettings.json is read next to the binary, never from the current directory.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.Logging.AddFilter("Microsoft", LogLevel.Warning).AddFilter("System", LogLevel.Warning).AddFilter("TestGen", LogLevel.Information);
Program.ConfigureServices(builder.Services, builder.Configuration);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

try
{
    using var host = builder.Build();
    host.Services.GetRequiredService<IStartupValidator>().Validate();
    return await host.Services.GetRequiredService<TestGenRunner>().RunAsync(cts.Token);
}
catch (OptionsValidationException ex)
{
    AnsiConsole.MarkupLine($"[red]Invalid configuration:[/]\n{Markup.Escape(string.Join('\n', ex.Failures))}");
    return 2;
}
catch (PreflightException ex)
{
    AnsiConsole.MarkupLine($"[red]Preflight failed:[/] {Markup.Escape(ex.Message)}");
    return 3;
}
catch (OperationCanceledException)
{
    AnsiConsole.MarkupLine("[yellow]Cancelled. Run again to resume.[/]");
    return 130;
}
catch (Exception ex)
{
    AnsiConsole.WriteException(ex);
    return 1;
}

public static partial class Program
{
    public static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<TestGenOptions>()
            .Bind(configuration.GetSection(TestGenOptions.SectionName))
            // DataAnnotations would evaluate SolutionDir, which throws on an empty path => check emptiness first, outside it.
            .Validate(o => !string.IsNullOrWhiteSpace(o.SolutionPath), "TestGen:SolutionPath is required (absolute path to a .sln/.slnx).")
            .Validate(o => string.IsNullOrWhiteSpace(o.SolutionPath) || Validator.TryValidateObject(o, new ValidationContext(o), null, validateAllProperties: true),
                "TestGen: a setting is out of range (Parallel 0-64, MaxRetries 0-20, MinCoverage 0-100, TestTimeoutSeconds >= 1)")
            .Validate(o => File.Exists(o.SolutionPath) && (o.SolutionPath.EndsWith(".sln") || o.SolutionPath.EndsWith(".slnx")),
                "TestGen:SolutionPath must point to an existing .sln/.slnx file")
            .ValidateOnStart();
        services.AddLlm(configuration);
        services.AddSingleton<IDotnetRunner, DotnetRunner>();
        services.AddSingleton<ICoverageRunner>(sp => new CoverageRunner(sp.GetRequiredService<ILogger<CoverageRunner>>()));
        services.AddSingleton<IGitRunner>(sp => new GitRunner(sp.GetRequiredService<IOptions<TestGenOptions>>().Value.SolutionDir, sp.GetRequiredService<ILogger<GitRunner>>()));
        services.AddSingleton<TestGenRunner>();
    }
}
