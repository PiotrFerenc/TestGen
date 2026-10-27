using System.Text.RegularExpressions;

namespace TestGen.Llm;

public static partial class CodeBlockExtractor
{
    private static readonly string[] CodeStarts = ["using", "[", "public", "private", "internal", "namespace", "//"];

    [GeneratedRegex(@"```[ \t]*([\w#+-]*)[^\n]*\n(.*?)```", RegexOptions.Singleline)]
    private static partial Regex Fence();

    public static string Extract(string llmText)
    {
        var blocks = Fence().Matches(llmText);
        if (blocks.Count > 0)
        {
            var csharp = blocks
                .Where(m => m.Groups[1].Value.ToLowerInvariant() is "" or "csharp" or "cs" or "c#")
                .Select(m => m.Groups[2].Value.Trim())
                .ToList();
            return csharp.Count > 0 ? string.Join("\n\n", csharp) : blocks[0].Groups[2].Value.Trim();
        }

        // No fences: drop leading prose before the first line that looks like code.
        var lines = llmText.Split('\n');
        var start = Array.FindIndex(lines, l => CodeStarts.Any(l.TrimStart().StartsWith));
        return (start < 0 ? llmText : string.Join('\n', lines[start..])).Trim();
    }
}
