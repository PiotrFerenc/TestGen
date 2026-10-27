using System.Net.Http.Headers;
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
        {
            var o = sp.GetRequiredService<IOptions<LlmOptions>>().Value;
            client.BaseAddress = new Uri(o.BaseUrl.EndsWith('/') ? o.BaseUrl : o.BaseUrl + "/");
            client.Timeout = TimeSpan.FromSeconds(o.TimeoutSeconds);
            if (!string.IsNullOrWhiteSpace(o.ApiKey))
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", o.ApiKey);
            foreach (var (name, value) in o.Headers)
            {
                client.DefaultRequestHeaders.Remove(name);
                client.DefaultRequestHeaders.TryAddWithoutValidation(name, value);
            }
        });

        services.AddSingleton<TokenUsage>();
        services.AddSingleton<PromptBuilder>();
        services.AddSingleton<ILlmClient, OpenAiCompatibleLlmClient>();
        return services;
    }
}
