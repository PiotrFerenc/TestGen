using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace TestGen.Llm;

public static class LlmServiceCollectionExtensions
{
    public static IServiceCollection AddLlm(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<LlmOptions>()
            .Bind(configuration.GetSection(LlmOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddHttpClient(LlmOptions.HttpClientName, (sp, client) =>
            HttpClientHeaders.Apply(client, sp.GetRequiredService<IOptions<LlmOptions>>().Value));

        services.AddSingleton<TokenUsage>();
        services.AddSingleton<PromptBuilder>();
        services.AddSingleton<ILlmClient, OpenAiCompatibleLlmClient>();
        return services;
    }
}
