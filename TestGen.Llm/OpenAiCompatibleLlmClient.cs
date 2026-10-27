using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TestGen.Llm;

public sealed class OpenAiCompatibleLlmClient(
    IHttpClientFactory httpClientFactory,
    IOptions<LlmOptions> options,
    TokenUsage usage,
    ILogger<OpenAiCompatibleLlmClient> logger) : ILlmClient
{
    private readonly LlmOptions _options = options.Value;

    public async Task<LlmResponse> CompleteAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient(LlmOptions.HttpClientName);
        var body = new
        {
            model = _options.Model,
            messages = messages.Select(m => new { role = m.Role, content = m.Content }),
            temperature = _options.Temperature,
            max_tokens = _options.MaxOutputTokens,
            stream = false,
        };

        string text;
        HttpResponseMessage response;
        try
        {
            logger.LogDebug("LLM request: {Count} messages, ~{Tokens} tokens", messages.Count, TokenEstimator.Estimate(messages));
            response = await client.PostAsJsonAsync("chat/completions", body, ct);
            text = await response.Content.ReadAsStringAsync(ct);
        }
        catch (HttpRequestException ex)
        {
            throw new LlmException($"LLM request failed: {ex.Message}", ex.StatusCode, null, ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new LlmException($"LLM request timed out after {client.Timeout.TotalSeconds}s.", null, null, ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw Fail($"LLM returned {(int)response.StatusCode} {response.StatusCode}.", response, text);

            string? content = null, finish = null;
            int? promptTokens = null, completionTokens = null;
            try
            {
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
                {
                    var choice = choices[0];
                    if (choice.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                        content = c.GetString();
                    if (choice.TryGetProperty("finish_reason", out var f) && f.ValueKind == JsonValueKind.String)
                        finish = f.GetString();
                }
                if (root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
                {
                    if (u.TryGetProperty("prompt_tokens", out var p) && p.TryGetInt32(out var pv)) promptTokens = pv;
                    if (u.TryGetProperty("completion_tokens", out var ctk) && ctk.TryGetInt32(out var cv)) completionTokens = cv;
                }
            }
            catch (JsonException)
            {
                throw Fail("LLM returned invalid JSON.", response, text);
            }

            if (string.IsNullOrWhiteSpace(content))
                throw Fail("LLM returned empty content.", response, text);

            var result = new LlmResponse(content,
                promptTokens ?? TokenEstimator.Estimate(messages),
                completionTokens ?? TokenEstimator.Estimate(content),
                finish);
            usage.AddPrompt(result.PromptTokens);
            usage.AddCompletion(result.CompletionTokens);
            logger.LogInformation("LLM response: {Prompt}+{Completion} tokens, finish={Finish}", result.PromptTokens, result.CompletionTokens, finish);
            return result;
        }
    }

    private LlmException Fail(string message, HttpResponseMessage response, string body)
    {
        var snippet = body.Length > 500 ? body[..500] : body;
        logger.LogWarning("{Message} Body: {Body}", message, snippet);
        return new LlmException(message, response.StatusCode, snippet);
    }
}
