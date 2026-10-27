using Shouldly;
using TestGen.Llm;
using Xunit;

namespace TestGen.Tests.Llm;

public class TextHelpersTests
{
    [Theory]
    [InlineData("", 0)]
    [InlineData("abcd", 1)]
    [InlineData("abcde", 2)]
    public void Estimate_is_ceil_chars_over_four(string text, int expected) =>
        TokenEstimator.Estimate(text).ShouldBe(expected);

    [Fact]
    public void Estimate_messages_adds_four_per_message() =>
        TokenEstimator.Estimate([new ChatMessage("user", "abcd"), new ChatMessage("system", "")]).ShouldBe(1 + 4 + 0 + 4);

    [Fact]
    public void Extracts_csharp_fence() =>
        CodeBlockExtractor.Extract("Here you go:\n```csharp\nusing X;\n[Fact] public void A() {}\n```\nBye").ShouldBe("using X;\n[Fact] public void A() {}");

    [Fact]
    public void Extracts_plain_fence() =>
        CodeBlockExtractor.Extract("```\nvar a = 1;\n```").ShouldBe("var a = 1;");

    [Fact]
    public void Concatenates_multiple_csharp_blocks_and_ignores_others() =>
        CodeBlockExtractor.Extract("```cs\nA\n```\ntext\n```json\n{}\n```\n```csharp\nB\n```").ShouldBe("A\n\nB");

    [Fact]
    public void Without_fence_strips_leading_prose() =>
        CodeBlockExtractor.Extract("Sure! Here are the tests.\n\nusing Xunit;\npublic void T() {}\n").ShouldBe("using Xunit;\npublic void T() {}");

    [Fact]
    public void Without_fence_and_code_returns_trimmed() =>
        CodeBlockExtractor.Extract("  just text  ").ShouldBe("just text");
}
