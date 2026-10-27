using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace TestGen.Coverage;

public static class CoverageReportWriter
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static void WriteJson(CoverageSnapshot snapshot, string path)
    {
        EnsureDir(path);
        File.WriteAllText(path, JsonSerializer.Serialize(snapshot, Json));
    }

    public static CoverageSnapshot ReadJson(string path) =>
        JsonSerializer.Deserialize<CoverageSnapshot>(File.ReadAllText(path), Json)
        ?? throw new CoverageException($"Empty coverage JSON: {path}");

    public static void WriteHtml(CoverageSnapshot before, CoverageSnapshot? after, string path)
    {
        var cmp = CoverageDiff.Compare(before, after ?? before);
        var byName = before.Classes.Concat(after?.Classes ?? []).DistinctBy(c => c.FullName).ToDictionary(c => c.FullName);
        static string E(string s) => WebUtility.HtmlEncode(s);
        static string P(double r) => (r * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%";

        var sb = new StringBuilder("""
            <!DOCTYPE html><html><head><meta charset="utf-8"><title>Coverage report</title>
            <style>body{font-family:sans-serif;margin:2em}table{border-collapse:collapse}td,th{border:1px solid #ccc;padding:4px 8px;text-align:left}
            td.n{text-align:right}tr.total{font-weight:bold;background:#eee}.up{color:#080}.down{color:#c00}</style></head><body>
            """);
        sb.Append($"<h1>Coverage report</h1><p>Before: {before.Timestamp:u}{(after is null ? "" : $" &middot; After: {after.Timestamp:u}")}</p>");
        sb.Append("<table><tr><th>Class</th><th>File</th><th>Lines</th><th>Uncovered before</th><th>Uncovered after</th><th>Rate before</th><th>Rate after</th><th>Delta</th></tr>");

        void Row(ClassDelta d, string name, string file, int lines, string cls = "")
        {
            var deltaCls = d.Delta > 0 ? "up" : d.Delta < 0 ? "down" : "";
            sb.Append($"<tr class=\"{cls}\"><td>{E(name)}</td><td>{E(file)}</td><td class=\"n\">{lines}</td><td class=\"n\">{d.UncoveredBefore}</td>")
              .Append($"<td class=\"n\">{(after is null ? "" : d.UncoveredAfter)}</td><td class=\"n\">{P(d.RateBefore)}</td>")
              .Append($"<td class=\"n\">{(after is null ? "" : P(d.RateAfter))}</td><td class=\"n {deltaCls}\">{(after is null ? "" : P(d.Delta))}</td></tr>");
        }

        foreach (var d in cmp.Classes)
        {
            var c = byName[d.FullName];
            Row(d, c.DisplayName, c.SourceFile, c.TotalLines);
        }
        Row(cmp.Total, "Total", "", before.TotalLines, "total");
        sb.Append("</table></body></html>");

        EnsureDir(path);
        File.WriteAllText(path, sb.ToString());
    }

    static void EnsureDir(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir != null) Directory.CreateDirectory(dir);
    }
}
