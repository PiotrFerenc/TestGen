using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace TestGen.Analysis;

public static class CompilationChecker
{
    public static async Task<IReadOnlyList<string>> GetErrorsAsync(Project testProject, string filePath, string newText, CancellationToken ct)
    {
        var full = Path.GetFullPath(filePath);
        var doc = testProject.Documents.FirstOrDefault(d => d.FilePath is not null && Path.GetFullPath(d.FilePath) == full);
        var project = doc is not null
            ? doc.WithText(SourceText.From(newText)).Project
            : testProject.AddDocument(Path.GetFileName(full), SourceText.From(newText), filePath: full).Project;

        var compilation = await project.GetCompilationAsync(ct) ?? throw new InvalidOperationException($"No compilation for {project.Name}");
        return compilation.GetDiagnostics(ct)
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Where(d => !d.Location.IsInSource || (d.Location.SourceTree?.FilePath is { Length: > 0 } p && Path.GetFullPath(p) == full))
            .Select(d => $"{d.Id}: {d.GetMessage()} (line {d.Location.GetLineSpan().StartLinePosition.Line + 1})")
            .ToList();
    }
}
