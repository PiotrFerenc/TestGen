using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TestGen.Coverage;

public static partial class CoberturaParser
{
    public static CoverageSnapshot ParseFile(string path) => Parse(File.ReadAllText(path));

    public static CoverageSnapshot Parse(string xml)
    {
        var doc = XDocument.Parse(xml);
        var timestamp = ParseTimestamp(doc.Root?.Attribute("timestamp")?.Value);

        var classes = new Dictionary<string, ClassCoverage>();
        foreach (var cls in doc.Descendants("class"))
        {
            var fullName = (string?)cls.Attribute("name") ?? "";
            // Compiler-generated: <>c, <Foo>d__3, closures. Their lines are ignored.
            if (fullName.Length == 0 || fullName.Contains('<')) continue;

            var methods = cls.Element("methods")?.Elements("method")
                .Select(m => new MethodCoverage(
                    (string?)m.Attribute("name") ?? "",
                    (string?)m.Attribute("signature") ?? "",
                    ParseLines(m.Element("lines"))))
                .ToList() ?? [];

            // Class-level lines not attributed to any method go into a synthetic method.
            var known = methods.SelectMany(m => m.Lines).Select(l => l.Number).ToHashSet();
            var orphans = ParseLines(cls.Element("lines")).Where(l => !known.Contains(l.Number)).ToList();
            if (orphans.Count > 0) methods.Add(new MethodCoverage("(class)", "", orphans));

            var file = (string?)cls.Attribute("filename") ?? "";
            classes[fullName] = classes.TryGetValue(fullName, out var existing)
                ? existing with { Methods = MergeMethods(existing.Methods.Concat(methods)) }
                : Create(fullName, file, MergeMethods(methods));
        }

        return new CoverageSnapshot(timestamp, classes.Values.ToList());
    }

    // Cobertura spec says Unix milliseconds; dotnet-coverage writes seconds. Anything past year 9999 in seconds is ms.
    static DateTimeOffset ParseTimestamp(string? value)
    {
        if (!long.TryParse(value, out var ts) || ts < 0) return DateTimeOffset.UtcNow;
        try
        {
            return ts > 253402300799 ? DateTimeOffset.FromUnixTimeMilliseconds(ts) : DateTimeOffset.FromUnixTimeSeconds(ts);
        }
        catch (ArgumentOutOfRangeException) { return DateTimeOffset.UtcNow; }
    }

    static ClassCoverage Create(string fullName, string file, IReadOnlyList<MethodCoverage> methods)
    {
        var display = fullName.Replace('/', '.');
        var outer = fullName.Split('/')[0];
        var dot = outer.LastIndexOf('.');
        var ns = dot < 0 ? "" : outer[..dot];
        return new ClassCoverage(fullName, ns, display[(display.LastIndexOf('.') + 1)..], file, methods);
    }

    static List<MethodCoverage> MergeMethods(IEnumerable<MethodCoverage> methods) =>
        methods.GroupBy(m => (m.Name, m.Signature))
            .Select(g => g.Skip(1).Any()
                ? new MethodCoverage(g.Key.Name, g.Key.Signature, g.SelectMany(m => m.Lines)
                    .GroupBy(l => l.Number)
                    .Select(lg => lg.MaxBy(l => (l.Hits > 0, l.BranchesCovered))!)
                    .OrderBy(l => l.Number).ToList())
                : g.First())
            .ToList();

    static List<LineCoverage> ParseLines(XElement? lines) =>
        lines?.Elements("line").Select(l =>
        {
            var isBranch = string.Equals((string?)l.Attribute("branch"), "true", StringComparison.OrdinalIgnoreCase);
            var m = ConditionRegex().Match((string?)l.Attribute("condition-coverage") ?? "");
            return new LineCoverage(
                int.Parse((string?)l.Attribute("number") ?? "0"),
                int.TryParse((string?)l.Attribute("hits"), out var h) ? h : 0,
                isBranch,
                m.Success ? int.Parse(m.Groups[1].Value) : 0,
                m.Success ? int.Parse(m.Groups[2].Value) : 0);
        }).ToList() ?? [];

    [GeneratedRegex(@"\((\d+)/(\d+)\)")]
    private static partial Regex ConditionRegex();
}
