using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using TestGen.Llm;
using Xunit;

namespace TestGen.Tests.Llm;

public class LlmConfigTests
{
    private static ServiceProvider Build(Dictionary<string, string?> values)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return new ServiceCollection().AddLogging().AddLlm(config).BuildServiceProvider();
    }

    private static Dictionary<string, string?> Valid() => new()
    {
        ["Llm:BaseUrl"] = "http://localhost:11434/v1",
        ["Llm:Model"] = "qwen2.5-coder:14b",
    };

    [Fact]
    public void Binds_values_and_defaults()
    {
        var values = Valid();
        values["Llm:Temperature"] = "0.5";
        values["Llm:MaxPromptTokens"] = "1234";
        using var sp = Build(values);

        var o = sp.GetRequiredService<IOptions<LlmOptions>>().Value;

        o.Model.ShouldBe("qwen2.5-coder:14b");
        o.Temperature.ShouldBe(0.5);
        o.MaxPromptTokens.ShouldBe(1234);
        o.TimeoutSeconds.ShouldBe(300);
        o.MaxOutputTokens.ShouldBe(4096);
        o.SystemPrompt.ShouldStartWith("Jesteś ekspertem");
        sp.GetRequiredService<ILlmClient>().ShouldBeOfType<OpenAiCompatibleLlmClient>();
        sp.GetRequiredService<TokenUsage>().ShouldBeSameAs(sp.GetRequiredService<TokenUsage>());
        sp.GetRequiredService<PromptBuilder>().ShouldNotBeNull();
    }

    [Theory]
    [InlineData("Llm:Model", "Llm:Model is required")]
    [InlineData("Llm:BaseUrl", "Llm:BaseUrl is required")]
    public void ValidateOnStart_fails_for_missing_required(string key, string expected)
    {
        var values = Valid();
        values.Remove(key);
        using var sp = Build(values);

        var ex = Should.Throw<OptionsValidationException>(() => sp.GetRequiredService<IStartupValidator>().Validate());

        ex.Message.ShouldContain(expected);
    }

    [Fact]
    public void ValidateOnStart_fails_for_relative_url()
    {
        var values = Valid();
        values["Llm:BaseUrl"] = "localhost/v1";
        using var sp = Build(values);

        Should.Throw<OptionsValidationException>(() => sp.GetRequiredService<IStartupValidator>().Validate())
            .Message.ShouldContain("absolute");
    }

    [Fact]
    public void ValidateOnStart_passes_for_valid_config()
    {
        using var sp = Build(Valid());

        Should.NotThrow(() => sp.GetRequiredService<IStartupValidator>().Validate());
    }

    [Fact]
    public void HttpClient_without_api_key_has_no_authorization()
    {
        using var sp = Build(Valid());

        var client = sp.GetRequiredService<IHttpClientFactory>().CreateClient(LlmOptions.HttpClientName);

        client.BaseAddress.ShouldBe(new Uri("http://localhost:11434/v1/"));
        client.DefaultRequestHeaders.Authorization.ShouldBeNull();
        client.Timeout.ShouldBe(TimeSpan.FromSeconds(300));
    }

    [Fact]
    public void HttpClient_with_api_key_headers_and_timeout()
    {
        var values = Valid();
        values["Llm:ApiKey"] = "secret";
        values["Llm:TimeoutSeconds"] = "42";
        values["Llm:Headers:X-Custom"] = "abc";
        using var sp = Build(values);

        var client = sp.GetRequiredService<IHttpClientFactory>().CreateClient(LlmOptions.HttpClientName);

        client.DefaultRequestHeaders.Authorization!.ToString().ShouldBe("Bearer secret");
        client.DefaultRequestHeaders.GetValues("X-Custom").ShouldBe(["abc"]);
        client.Timeout.ShouldBe(TimeSpan.FromSeconds(42));
    }
}
