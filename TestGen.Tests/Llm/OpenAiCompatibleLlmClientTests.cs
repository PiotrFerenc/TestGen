using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using TestGen.Llm;
using Xunit;

namespace TestGen.Tests.Llm;

public class OpenAiCompatibleLlmClientTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? Request;
        public string? Body;
        public long? LengthBeforeRead;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            LengthBeforeRead = request.Content?.Headers.ContentLength; // before buffering, as a real socket sees it
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return respond(request);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private readonly TokenUsage _usage = new();
    private static readonly ChatMessage[] Messages = [ChatMessage.System("sys"), ChatMessage.User("hello world")];

    private (OpenAiCompatibleLlmClient, FakeHandler) Create(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new FakeHandler(respond);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(LlmOptions.HttpClientName).Returns(_ => new HttpClient(handler) { BaseAddress = new Uri("http://llm/v1/") });
        var options = Options.Create(new LlmOptions { BaseUrl = "http://llm/v1/", Model = "m1", Temperature = 0.3, MaxOutputTokens = 99 });
        return (new OpenAiCompatibleLlmClient(factory, options, _usage, NullLogger<OpenAiCompatibleLlmClient>.Instance), handler);
    }

    private const string Ok = """
        {"choices":[{"message":{"role":"assistant","content":"```csharp\nx\n```"},"finish_reason":"stop"}],
         "usage":{"prompt_tokens":11,"completion_tokens":7}}
        """;

    [Fact]
    public async Task Posts_to_chat_completions_with_expected_body()
    {
        var (client, handler) = Create(_ => Json(HttpStatusCode.OK, Ok));

        await client.CompleteAsync(Messages, CancellationToken.None);

        handler.Request!.Method.ShouldBe(HttpMethod.Post);
        handler.LengthBeforeRead.ShouldBe(Encoding.UTF8.GetByteCount(handler.Body!)); // not chunked
        handler.Request.Content!.Headers.ContentType!.MediaType.ShouldBe("application/json");
        handler.Request.RequestUri!.AbsoluteUri.ShouldBe("http://llm/v1/chat/completions");
        using var doc = JsonDocument.Parse(handler.Body!);
        var root = doc.RootElement;
        root.GetProperty("model").GetString().ShouldBe("m1");
        root.GetProperty("temperature").GetDouble().ShouldBe(0.3);
        root.GetProperty("max_tokens").GetInt32().ShouldBe(99);
        root.GetProperty("stream").GetBoolean().ShouldBeFalse();
        var msgs = root.GetProperty("messages");
        msgs.GetArrayLength().ShouldBe(2);
        msgs[1].GetProperty("role").GetString().ShouldBe("user");
        msgs[1].GetProperty("content").GetString().ShouldBe("hello world");
    }

    [Fact]
    public async Task Parses_response_and_tracks_usage()
    {
        var (client, _) = Create(_ => Json(HttpStatusCode.OK, Ok));

        var r1 = await client.CompleteAsync(Messages, CancellationToken.None);
        await client.CompleteAsync(Messages, CancellationToken.None);

        r1.ShouldBe(new LlmResponse("```csharp\nx\n```", 11, 7, "stop"));
        _usage.Requests.ShouldBe(2);
        _usage.PromptTokens.ShouldBe(22);
        _usage.CompletionTokens.ShouldBe(14);
        _usage.Total.ShouldBe(36);
    }

    [Fact]
    public async Task Missing_usage_is_estimated()
    {
        var (client, _) = Create(_ => Json(HttpStatusCode.OK, """{"choices":[{"message":{"content":"12345678"}}]}"""));

        var r = await client.CompleteAsync(Messages, CancellationToken.None);

        r.PromptTokens.ShouldBe(TokenEstimator.Estimate(Messages));
        r.CompletionTokens.ShouldBe(2);
        r.FinishReason.ShouldBeNull();
    }

    [Fact]
    public async Task Server_error_throws_with_status_and_snippet()
    {
        var (client, _) = Create(_ => Json(HttpStatusCode.InternalServerError, new string('e', 1000)));

        var ex = await Should.ThrowAsync<LlmException>(() => client.CompleteAsync(Messages, CancellationToken.None));

        ex.Status.ShouldBe(HttpStatusCode.InternalServerError);
        ex.BodySnippet!.Length.ShouldBe(500);
        _usage.Requests.ShouldBe(0);
    }

    [Fact]
    public async Task Empty_choices_throws()
    {
        var (client, _) = Create(_ => Json(HttpStatusCode.OK, """{"choices":[]}"""));

        var ex = await Should.ThrowAsync<LlmException>(() => client.CompleteAsync(Messages, CancellationToken.None));

        ex.Message.ShouldContain("empty");
    }

    [Fact]
    public async Task Transport_failure_is_wrapped()
    {
        var (client, _) = Create(_ => throw new HttpRequestException("connection refused"));

        var ex = await Should.ThrowAsync<LlmException>(() => client.CompleteAsync(Messages, CancellationToken.None));

        ex.InnerException.ShouldBeOfType<HttpRequestException>();
    }

    [Fact]
    public async Task Timeout_is_wrapped()
    {
        var (client, _) = Create(_ => throw new TaskCanceledException("timeout"));

        var ex = await Should.ThrowAsync<LlmException>(() => client.CompleteAsync(Messages, CancellationToken.None));

        ex.Message.ShouldContain("timed out");
    }
}
