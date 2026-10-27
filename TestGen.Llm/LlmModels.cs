using System.Net;

namespace TestGen.Llm;

public record ChatMessage(string Role, string Content)
{
    public static ChatMessage System(string content) => new("system", content);
    public static ChatMessage User(string content) => new("user", content);
    public static ChatMessage Assistant(string content) => new("assistant", content);
}

public record LlmResponse(string Content, int PromptTokens, int CompletionTokens, string? FinishReason);

public class LlmException(string message, HttpStatusCode? status = null, string? bodySnippet = null, Exception? inner = null)
    : Exception(message, inner)
{
    public HttpStatusCode? Status { get; } = status;
    public string? BodySnippet { get; } = bodySnippet;
}

public record UncoveredMethod(string Name, int StartLine, int EndLine, IReadOnlyList<int> UncoveredLines);

public record GenerationRequest(
    string ClassFullName,
    string ClassSource,
    string ClassSourceTrimmed,
    IReadOnlyList<UncoveredMethod> UncoveredMethods,
    string? ExistingTestFile,
    string ConventionsSummary,
    IReadOnlyList<string> DependencySignatures,
    IReadOnlyList<string> CallPathHints,
    string? TestClassName,
    string? TestNamespace);

public interface ILlmClient
{
    Task<LlmResponse> CompleteAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct);
}

/// <summary>Process-wide token counters. Each AddPrompt call counts as one request.</summary>
public sealed class TokenUsage
{
    private long _prompt, _completion, _requests;

    public void AddPrompt(int tokens)
    {
        Interlocked.Add(ref _prompt, tokens);
        Interlocked.Increment(ref _requests);
    }

    public void AddCompletion(int tokens) => Interlocked.Add(ref _completion, tokens);

    public long PromptTokens => Interlocked.Read(ref _prompt);
    public long CompletionTokens => Interlocked.Read(ref _completion);
    public long Requests => Interlocked.Read(ref _requests);
    public long Total => PromptTokens + CompletionTokens;
}

public static class TokenEstimator
{
    public static int Estimate(string text) => (text.Length + 3) / 4;

    public static int Estimate(IEnumerable<ChatMessage> messages) => messages.Sum(m => Estimate(m.Content) + 4);
}
