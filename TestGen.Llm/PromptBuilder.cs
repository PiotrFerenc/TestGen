using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace TestGen.Llm;

public sealed class PromptBuilder(IOptions<LlmOptions> options)
{
    public const string TruncatedMarker = "// ... truncated ...";
    private const int MaxErrorLines = 40;

    private const string HardRules = """

        Hard rules:
        - Output ONLY new test methods plus any using directives and fields they need, in ONE ```csharp block. A class wrapper is not required.
        - Never modify, rename or remove existing tests.
        - No [Skip], no Skip= parameters, no Assert.True(true); every test must assert something meaningful.
        - Use the test framework, mocking library and assertion library from the conventions.
        - Mock external dependencies (database, HTTP, file system, time) with the detected mocking library.
        - Follow the existing test method naming pattern.
        """;

    private readonly LlmOptions _options = options.Value;

    public IReadOnlyList<ChatMessage> BuildGeneration(GenerationRequest request)
    {
        var system = ChatMessage.System(_options.SystemPrompt + HardRules);
        var max = _options.MaxPromptTokens;
        var state = new State(request.ClassSource, request.DependencySignatures, request.CallPathHints, request.ExistingTestFile);

        bool Fits() => Measure(state) <= max;
        int Measure(State s) => TokenEstimator.Estimate([system, ChatMessage.User(Render(request, s))]);

        if (!Fits()) state = state with { ClassSource = request.ClassSourceTrimmed };
        if (!Fits()) state = state with { Dependencies = [] };
        if (!Fits()) state = state with { Hints = [] };
        if (!Fits() && state.TestFile is not null)
            state = state with { TestFile = TruncateHead(state.TestFile, BudgetChars(Measure(state with { TestFile = TruncatedMarker }), max), keepClassHeader: true) };
        if (!Fits())
        {
            // Cut everything but the uncovered methods first; only then cut the tail.
            var budget = BudgetChars(Measure(state with { ClassSource = TruncatedMarker }), max);
            state = state with { ClassSource = KeepUncovered(state.ClassSource, request.UncoveredMethods, budget) };
            if (!Fits()) state = state with { ClassSource = TruncateHead(state.ClassSource, budget, keepClassHeader: false) };
        }

        return [system, ChatMessage.User(Render(request, state))];
    }

    public IReadOnlyList<ChatMessage> BuildRepair(IReadOnlyList<ChatMessage> previous, string previousCode, IReadOnlyList<string> errors)
    {
        var errorLines = errors.SelectMany(e => e.Split('\n')).Where(l => l.Trim().Length > 0).ToList();
        var sb = new StringBuilder("The code above does not compile or its tests fail:\n```\n");
        foreach (var line in errorLines.Take(MaxErrorLines)) sb.AppendLine(line.TrimEnd());
        if (errorLines.Count > MaxErrorLines) sb.AppendLine($"... ({errorLines.Count - MaxErrorLines} more lines)");
        sb.Append("```\nFix the errors and return the corrected FULL snippet (all new test methods, usings and fields) in ONE ```csharp block. Follow the same hard rules.");

        var messages = previous.ToList();
        messages.Add(ChatMessage.Assistant($"```csharp\n{previousCode}\n```"));
        messages.Add(ChatMessage.User(sb.ToString()));

        // Keep system + first user; drop oldest assistant/user repair pairs, never the newest one.
        while (messages.Count > 4 && TokenEstimator.Estimate(messages) > _options.MaxPromptTokens)
            messages.RemoveRange(2, 2);
        return messages;
    }

    private sealed record State(string ClassSource, IReadOnlyList<string> Dependencies, IReadOnlyList<string> Hints, string? TestFile);

    private static int BudgetChars(int tokensWithoutText, int max) => Math.Max(0, (max - tokensWithoutText) * 4);

    // ponytail: chars/4 budget, same heuristic as TokenEstimator; good enough for a local model's context window.
    private static string TruncateHead(string text, int budgetChars, bool keepClassHeader)
    {
        var lines = text.Split('\n');
        var mustKeep = keepClassHeader ? Array.FindIndex(lines, l => l.Contains(" class ") || l.TrimStart().StartsWith("class ")) : -1;
        var sb = new StringBuilder();
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > mustKeep && sb.Length + lines[i].Length + 1 > budgetChars) break;
            sb.Append(lines[i]).Append('\n');
        }
        return sb.Append(TruncatedMarker).ToString();
    }

    // ponytail: StartLine/EndLine are file lines, ClassSource is a reconstruction, so the method is found by its
    // declaration line (first "<type> Name(" not preceded by return/await/throw) and kept for EndLine-StartLine+1 lines.
    private static string KeepUncovered(string text, IReadOnlyList<UncoveredMethod> methods, int budgetChars)
    {
        var lines = text.Split('\n');
        var keep = new bool[lines.Length];
        foreach (var m in methods)
        {
            var decl = new Regex($@"^\s*(?!(return|await|throw)\b)[\w\s<>\[\],.?]*\s{Regex.Escape(m.Name)}\s*(<[^>]*>)?\s*\(");
            var start = Array.FindIndex(lines, l => decl.IsMatch(l));
            if (start < 0) continue;
            for (var i = start; i < Math.Min(lines.Length, start + Math.Max(1, m.EndLine - m.StartLine + 1)); i++) keep[i] = true;
        }

        var markerCost = TruncatedMarker.Length + 1;
        var remaining = budgetChars - lines.Where((_, i) => keep[i]).Sum(l => l.Length + 1)
            - markerCost * (keep.Where((k, i) => k && (i == 0 || !keep[i - 1])).Count() + 1); // max markers: one per gap
        var sb = new StringBuilder();
        var cutting = false;
        for (var i = 0; i < lines.Length; i++)
        {
            if (!keep[i] && !cutting && lines[i].Length + 1 <= remaining) remaining -= lines[i].Length + 1;
            else if (!keep[i])
            {
                if (!cutting) sb.Append(TruncatedMarker).Append('\n');
                cutting = true;
                continue;
            }
            else if (cutting && remaining > 0) cutting = false; // after a kept method, resume filling if budget allows
            sb.Append(lines[i]).Append('\n');
        }
        return sb.ToString().TrimEnd('\n');
    }

    private static string Render(GenerationRequest r, State s)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Conventions").AppendLine(r.ConventionsSummary).AppendLine();
        sb.AppendLine($"## Class under test: `{r.ClassFullName}`").AppendLine("```csharp").AppendLine(s.ClassSource).AppendLine("```").AppendLine();

        sb.AppendLine("## Uncovered methods");
        foreach (var m in r.UncoveredMethods)
            sb.AppendLine($"- `{m.Name}` (lines {m.StartLine}-{m.EndLine}), uncovered lines: {string.Join(", ", m.UncoveredLines)}");
        sb.AppendLine();

        if (s.Dependencies.Count > 0)
            sb.AppendLine("## Dependency signatures (mock these)").AppendLine("```csharp").AppendLine(string.Join("\n", s.Dependencies)).AppendLine("```").AppendLine();

        if (s.Hints.Count > 0)
        {
            sb.AppendLine("## Call-path hints (reach private/internal code through these public entry points)");
            foreach (var h in s.Hints) sb.AppendLine($"- {h}");
            sb.AppendLine();
        }

        if (s.TestFile is not null)
            sb.AppendLine("## Existing test file (do not modify existing tests)").AppendLine("```csharp").AppendLine(s.TestFile).AppendLine("```").AppendLine();
        else
            sb.AppendLine($"No test file exists yet; target class `{r.TestClassName}` in namespace `{r.TestNamespace}`.").AppendLine();

        sb.Append("Write new test methods that cover the uncovered lines listed above.");
        return sb.ToString();
    }
}
