using Microsoft.Extensions.Options;
using Shouldly;
using TestGen.Llm;
using Xunit;

namespace TestGen.Tests.Llm;

public class PromptBuilderTests
{
    private static PromptBuilder Builder(int maxTokens = 8000) =>
        new(Options.Create(new LlmOptions { BaseUrl = "http://x/", Model = "m", MaxPromptTokens = maxTokens, SystemPrompt = "SYS" }));

    private static readonly string FullSource = "public class Calc\n{\n" + string.Join("\n", Enumerable.Range(0, 200).Select(i => $"    // FULLBODY line {i}")) + "\n}";
    private const string TrimmedSource = "public class Calc { /* TRIMMED */ }";
    private static readonly string TestFile = "using Xunit;\nnamespace T;\npublic class CalcTests\n{\n" + string.Join("\n", Enumerable.Range(0, 200).Select(i => $"    // TESTLINE {i}")) + "\n}";

    private static GenerationRequest Request(string? testFile = null) => new(
        "App.Calc", FullSource, TrimmedSource,
        [new UncoveredMethod("Add", 10, 20, [12, 13])],
        testFile, "CONVENTIONS: xunit + NSubstitute + Shouldly",
        ["DEPSIG: interface IClock { DateTime Now { get; } }"],
        ["HINT: Calc.Run -> Calc.Add"],
        "CalcTests", "App.Tests");

    private static string User(IReadOnlyList<ChatMessage> m) => m[1].Content;

    [Fact]
    public void Contains_all_sections()
    {
        var messages = Builder().BuildGeneration(Request(TestFile));

        messages.Count.ShouldBe(2);
        messages[0].Role.ShouldBe("system");
        messages[0].Content.ShouldStartWith("SYS");
        messages[0].Content.ShouldContain("ONE ```csharp block");
        messages[1].Role.ShouldBe("user");
        var user = User(messages);
        user.ShouldContain("CONVENTIONS:");
        user.ShouldContain("App.Calc");
        user.ShouldContain("FULLBODY line 199");
        user.ShouldContain("`Add` (lines 10-20), uncovered lines: 12, 13");
        user.ShouldContain("DEPSIG:");
        user.ShouldContain("HINT:");
        user.ShouldContain("TESTLINE 199");
        user.ShouldNotContain("TRIMMED");
    }

    [Fact]
    public void No_test_file_variant()
    {
        var user = User(Builder().BuildGeneration(Request()));

        user.ShouldContain("No test file exists yet; target class `CalcTests` in namespace `App.Tests`");
    }

    private static int Size(GenerationRequest r) => TokenEstimator.Estimate(Builder().BuildGeneration(r));

    [Fact]
    public void Trimming_first_swaps_class_source()
    {
        var full = Size(Request(TestFile));
        var user = User(Builder(full - 1).BuildGeneration(Request(TestFile)));

        user.ShouldContain("TRIMMED");
        user.ShouldNotContain("FULLBODY");
        user.ShouldContain("DEPSIG:");
        user.ShouldContain("HINT:");
        user.ShouldContain("TESTLINE 199");
    }

    [Fact]
    public void Trimming_then_drops_dependencies_then_hints()
    {
        var trimmed = Size(Request(TestFile) with { ClassSource = TrimmedSource });

        var user = User(Builder(trimmed - 1).BuildGeneration(Request(TestFile)));
        user.ShouldNotContain("DEPSIG:");
        user.ShouldContain("HINT:");

        var noDeps = Size(Request(TestFile) with { ClassSource = TrimmedSource, DependencySignatures = [] });
        user = User(Builder(noDeps - 1).BuildGeneration(Request(TestFile)));
        user.ShouldNotContain("HINT:");
        user.ShouldContain("TESTLINE 199");
    }

    [Fact]
    public void Trimming_then_truncates_test_file_head_kept()
    {
        var minimal = Size(Request(TestFile) with { ClassSource = TrimmedSource, DependencySignatures = [], CallPathHints = [] });
        var builder = Builder(minimal - 500);

        var messages = builder.BuildGeneration(Request(TestFile));
        var user = User(messages);

        user.ShouldContain("public class CalcTests");
        user.ShouldContain("TESTLINE 0");
        user.ShouldNotContain("TESTLINE 199");
        user.ShouldContain(PromptBuilder.TruncatedMarker);
        user.ShouldContain("TRIMMED");
        TokenEstimator.Estimate(messages).ShouldBeLessThanOrEqualTo(minimal - 500);
    }

    [Fact]
    public void Trimming_finally_truncates_class_source_tail()
    {
        var request = Request() with { ClassSourceTrimmed = FullSource };
        var minimal = Size(request with { DependencySignatures = [], CallPathHints = [] });

        var user = User(Builder(minimal - 200).BuildGeneration(request));

        user.ShouldContain("FULLBODY line 0");
        user.ShouldNotContain("FULLBODY line 199");
        user.ShouldContain(PromptBuilder.TruncatedMarker);
    }

    [Fact]
    public void BuildRepair_appends_assistant_code_and_capped_errors()
    {
        var builder = Builder();
        var initial = builder.BuildGeneration(Request());
        var errors = Enumerable.Range(0, 50).Select(i => $"CS0103 error {i}").ToList();

        var repaired = builder.BuildRepair(initial, "public void A() {}", errors);

        repaired.Count.ShouldBe(4);
        repaired[2].Role.ShouldBe("assistant");
        repaired[2].Content.ShouldBe("```csharp\npublic void A() {}\n```");
        repaired[3].Role.ShouldBe("user");
        repaired[3].Content.ShouldContain("CS0103 error 39");
        repaired[3].Content.ShouldNotContain("CS0103 error 40");
        repaired[3].Content.ShouldContain("10 more lines");
        repaired[3].Content.ShouldContain("ONE ```csharp block");
    }

    [Fact]
    public void BuildRepair_drops_oldest_pair_when_over_budget()
    {
        var initial = Builder().BuildGeneration(Request());
        var first = Builder().BuildRepair(initial, "FIRST " + new string('x', 400), ["err1"]);
        var max = TokenEstimator.Estimate(first) + 20;

        var second = Builder(max).BuildRepair(first, "SECOND", ["err2"]);

        second.Count.ShouldBe(4);
        second[0].ShouldBe(initial[0]);
        second[1].ShouldBe(initial[1]);
        second[2].Content.ShouldContain("SECOND");
        second[3].Content.ShouldContain("err2");
        second.ShouldNotContain(m => m.Content.Contains("FIRST"));
    }
}
