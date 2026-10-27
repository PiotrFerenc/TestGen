using System.Net.Http.Headers;

namespace TestGen.Llm;

/// <summary>Applies base address, timeout and configured headers to a named HttpClient.</summary>
public static class HttpClientHeaders
{
    public const string ApiKeyPlaceholder = "{ApiKey}";

    public static void Apply(HttpClient client, LlmOptions options)
    {
        client.BaseAddress = new Uri(options.BaseUrl.EndsWith('/') ? options.BaseUrl : options.BaseUrl + "/");
        client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);

        var hasApiKey = !string.IsNullOrWhiteSpace(options.ApiKey);
        // Default auth scheme; a configured "Authorization" header below overrides it.
        if (hasApiKey)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);

        foreach (var (name, rawValue) in options.Headers)
        {
            var value = hasApiKey ? rawValue.Replace(ApiKeyPlaceholder, options.ApiKey) : rawValue;
            client.DefaultRequestHeaders.Remove(name);
            client.DefaultRequestHeaders.TryAddWithoutValidation(name, value);
        }
    }
}
