using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using TestGen.Analysis;
using TestGen.Coverage;
using TestGen.Git;
using TestGen.Llm;
using TestGen.Queue;

namespace TestGen.Cli.Worker;

/// <summary>Generates tests for one work item in its own git worktree/branch; see PLAN.md section 6.</summary>
public sealed class WorkItemProcessor(
    Solution solution, TestConventions conventions, CoverageSnapshot coverageBefore,
    IGitRunner repoGit, ILlmClient llm, PromptBuilder prompts, ICoverageRunner coverage, IDotnetRunner dotnet,
    TestGenOptions options, ILogger<WorkItemProcessor> logger) : IWorkItemProcessor
{
    static readonly string[] UnmockableSuffixes = ["DbContext", "HttpClient", "HttpContext"];
    const int MaxHints = 10;

    public async Task<WorkResult> ProcessAsync(WorkItem item, CancellationToken ct)
    {
        var branch = TestGenOptions.BranchPrefix + item.Id;
        var wtDir = Path.Combine(options.WorkDir, "wt", item.Id);
        var run = new Run(item);
        try
        {
            await CleanupAsync(wtDir, branch, ct);
            logger.LogInformation("{Id}: creating worktree {Dir} on {Branch}", item.Id, wtDir, branch);
            var worktree = await repoGit.WorktreeAddAsync(wtDir, branch, TestGenOptions.ResultBranch, ct);

            var repoRoot = await repoGit.GetRepoRootAsync(ct);
            string Map(string p)
            {
                var rel = RepoRelative(repoRoot, p);
                if (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel))
                    throw new InvalidOperationException($"{p} is outside the repository {repoRoot}; cannot map it into the worktree");
                return Path.Combine(wtDir, rel);
            }
            var testProject = solution.Projects.FirstOrDefault(p => SamePath(p.FilePath, item.TestProjectPath))
                              ?? throw new InvalidOperationException($"Test project {item.TestProjectPath} not in solution");
            var ctx = new ItemContext(item, testProject, Map(item.TestProjectPath), Map(item.TargetTestFile));

            var succeeded = new List<WorkClass>();
            var skipped = new List<string>();
            var failed = new List<string>();
            foreach (var cls in item.Classes)
            {
                var outcome = await ProcessClassAsync(ctx, cls, run, ct);
                if (outcome is null) succeeded.Add(cls);
                else if (outcome.Skipped) skipped.Add($"{cls.FullName}: {outcome.Reason}");
                else failed.Add($"{cls.FullName}: {outcome.Reason}");
            }

            if (succeeded.Count == 0)
            {
                await RollbackAsync(item.Id, wtDir, branch);
                return failed.Count == 0
                    ? run.Result(WorkStatus.Skipped, string.Join("; ", skipped))
                    : run.Result(WorkStatus.Failed, string.Join("; ", failed.Concat(skipped)));
            }

            logger.LogInformation("{Id}: checking changed files", item.Id);
            var changed = await ChangedFilesAsync(worktree, ct);
            var testDirs = SolutionLoader.TestProjects(solution).Where(p => p.FilePath is not null)
                .Select(p => RepoRelative(repoRoot, Path.GetDirectoryName(p.FilePath)!)).ToList();
            var violations = OutsideTestProjects(changed, testDirs);
            if (violations.Count > 0) return await FailAsync(run, wtDir, branch, string.Join("; ", violations));

            logger.LogInformation("{Id}: measuring coverage", item.Id);
            // Same scope as the baseline (whole solution): classes covered by other test projects must not read as 0.
            var xml = await coverage.CollectAsync(Map(Path.GetFullPath(options.SolutionPath)), Path.Combine(wtDir, ".testgen-coverage.xml"), options.TestTimeout, ct, noBuild: false);
            var after = CoberturaParser.ParseFile(xml);
            File.Delete(xml);
            var gained = succeeded.Any(c => (CoverageDiff.RateFor(after, c.FullName) ?? 0) > (CoverageDiff.RateFor(coverageBefore, c.FullName) ?? item.CoverageBefore ?? 0));
            if (!gained) return await FailAsync(run, wtDir, branch, "no coverage gain");
            var mine = after.Classes.Where(c => succeeded.Any(s => s.FullName == c.FullName)).ToList();
            var total = mine.Sum(c => c.TotalLines);
            double? coverageAfter = total == 0 ? null : (double)mine.Sum(c => c.CoveredLines) / total;

            if (options.RunNewTestsTwice)
            {
                logger.LogInformation("{Id}: re-running tests for flakiness", item.Id);
                var again = await dotnet.TestAsync(ctx.TestProjectPath, options.TestTimeout, ct, noBuild: true);
                if (!again.Success) return await FailAsync(run, wtDir, branch, "flaky");
            }

            logger.LogInformation("{Id}: committing {Files}", item.Id, string.Join(", ", changed));
            await worktree.AddAsync(changed, ct);
            await worktree.CommitAsync("testgen: tests for " + string.Join(", ", succeeded.Select(c => c.FullName)), ct);
            await repoGit.WorktreeRemoveAsync(wtDir, force: true, ct);
            await repoGit.WorktreePruneAsync(ct);
            var reason = failed.Count + skipped.Count == 0 ? null : string.Join("; ", failed.Concat(skipped));
            return run.Result(WorkStatus.Done, reason, branch, coverageAfter);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await RollbackAsync(item.Id, wtDir, branch);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{Id}: worker failed", item.Id);
            return await FailAsync(run, wtDir, branch, ex.Message);
        }
    }

    sealed record ItemContext(WorkItem Item, Project TestProject, string TestProjectPath, string WorktreeTestFile)
    {
        /// <summary>Test class created in the target file by an earlier class of this item.</summary>
        public string? CreatedTestClass { get; set; }
    }

    sealed record ClassOutcome(bool Skipped, string Reason);

    sealed class Run(WorkItem item)
    {
        public int Prompt, Completion;

        public WorkResult Result(WorkStatus status, string? reason, string? branch = null, double? coverageAfter = null) =>
            new(status, reason, branch, Prompt, Completion, coverageAfter);

        public string Id => item.Id;
    }

    /// <summary>Returns null on success, otherwise why the class was skipped/failed. A non-null outcome restores the test file.</summary>
    async Task<ClassOutcome?> ProcessClassAsync(ItemContext ctx, WorkClass cls, Run run, CancellationToken ct)
    {
        var snapshot = File.Exists(ctx.WorktreeTestFile) ? await File.ReadAllTextAsync(ctx.WorktreeTestFile, ct) : null;
        ClassOutcome? outcome;
        try
        {
            outcome = await GenerateAsync(ctx, cls, run, snapshot, ct);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            logger.LogWarning(ex, "{Id}: {Class} failed", ctx.Item.Id, cls.FullName);
            outcome = new(false, ex.Message);
        }
        if (outcome is not null)
        {
            // Restore the file as it was before this class.
            if (snapshot is null) File.Delete(ctx.WorktreeTestFile);
            else await File.WriteAllTextAsync(ctx.WorktreeTestFile, snapshot, ct);
        }
        return outcome;
    }

    async Task<ClassOutcome?> GenerateAsync(ItemContext ctx, WorkClass cls, Run run, string? snapshot, CancellationToken ct)
    {
        var id = ctx.Item.Id;
        logger.LogInformation("{Id}: generating tests for {Class}", id, cls.FullName);
        var type = await TypeLocator.FindTypeAsync(solution, cls.FullName, ct);
        if (type is null) return new(true, "type not found in solution");
        var classCoverage = coverageBefore.Classes.FirstOrDefault(c => c.FullName == cls.FullName);
        if (classCoverage is null) return new(true, "class not in coverage report");

        var deps = DependencyCollector.Collect(type);
        var unmockable = deps.CtorParams.FirstOrDefault(IsUnmockable);
        if (unmockable is not null) return new(true, $"unmockable dependency: {unmockable.TypeFullName}");

        var uncovered = classCoverage.Methods.Where(m => !m.IsFullyCovered).Select(m =>
        {
            var syntax = TypeLocator.FindMethod(type, m.Name, m.Signature);
            var (start, end) = syntax is null ? (m.StartLine, m.EndLine) : TypeLocator.LineSpan(syntax);
            return new UncoveredMethod(m.Name, start, end, m.UncoveredLineNumbers);
        }).ToList();
        var names = uncovered.Select(m => m.Name).ToHashSet();
        var source = TypeLocator.FullSource(type);

        var hints = new List<string>();
        foreach (var method in type.GetMembers().OfType<IMethodSymbol>().Where(m => names.Contains(m.Name) && m.DeclaredAccessibility != Accessibility.Public))
        {
            if (hints.Count >= MaxHints) break;
            hints.AddRange(await CallPathFinder.FindPathsAsync(solution, method, ct: ct));
        }

        string? testClassName = null, testNamespace = null;
        var target = await TestLocator.FindAsync(solution, type, ct);
        if (snapshot is null)
        {
            testClassName = target?.TestClassName ?? type.Name + "Tests";
            testNamespace = target?.Namespace ?? (type.ContainingNamespace.IsGlobalNamespace ? "" : type.ContainingNamespace.ToDisplayString());
        }
        // Existing file: insert into this type's test class, or the one an earlier class of this item created.
        var insertInto = snapshot is null ? testClassName
            : target is { Exists: true, TestClassName: { } existing } && SamePath(target.FilePath, ctx.Item.TargetTestFile) ? existing
            : ctx.CreatedTestClass;

        var request = new GenerationRequest(cls.FullName, source, SourceTrimmer.TrimMethodBodies(source, names), uncovered, snapshot,
            conventions.Summarize(), deps.MemberSignatures, hints.Take(MaxHints).ToList(), testClassName, testNamespace);
        var messages = prompts.BuildGeneration(request);

        IReadOnlyList<string> errors = [];
        for (var attempt = 1; attempt <= options.MaxRetries + 1; attempt++)
        {
            logger.LogInformation("{Id}: {Class} attempt {Attempt}", id, cls.FullName, attempt);
            var response = await llm.CompleteAsync(messages, ct);
            run.Prompt += response.PromptTokens;
            run.Completion += response.CompletionTokens;
            var code = CodeBlockExtractor.Extract(response.Content);

            errors = await TryApplyAsync(ctx, code, snapshot, insertInto, testNamespace ?? "", ct);
            if (errors.Count == 0)
            {
                if (snapshot is null) ctx.CreatedTestClass = testClassName;
                return null;
            }
            logger.LogInformation("{Id}: {Class} attempt {Attempt} rejected: {Errors}", id, cls.FullName, attempt, string.Join(" | ", errors.Take(5)));
            messages = prompts.BuildRepair(messages, code, errors);
        }

        // One line per error, whitespace collapsed, so the reason fits the console table and state.json.
        var brief = errors.Take(3).Select(e => string.Join(' ', e.Split((char[])null!, StringSplitOptions.RemoveEmptyEntries)));
        return new(false, $"retries exhausted: {string.Join(" | ", brief)}");
    }

    /// <summary>Validates, inserts on top of <paramref name="original"/>, writes, builds and tests. Empty list = green.</summary>
    async Task<IReadOnlyList<string>> TryApplyAsync(ItemContext ctx, string code, string? original, string? className, string ns, CancellationToken ct)
    {
        var validation = SnippetValidator.Validate(code, conventions);
        if (!validation.IsValid) return validation.Errors;

        string newText;
        if (original is null)
            newText = TestInserter.CreateNewFile(className!, ns, conventions, validation.Parsed!);
        else
        {
            var existing = ctx.TestProject.Documents.FirstOrDefault(d => SamePath(d.FilePath, ctx.Item.TargetTestFile));
            var doc = existing is not null
                ? existing.WithText(SourceText.From(original))
                : ctx.TestProject.AddDocument(Path.GetFileName(ctx.Item.TargetTestFile), SourceText.From(original), filePath: ctx.Item.TargetTestFile);
            newText = await TestInserter.InsertAsync(doc, validation.Parsed!, ct, className);
        }

        // Forbidden patterns are checked on the snippet by SnippetValidator; the file may already contain e.g. [Skip].
        var errors = original is null ? [] : QualityGuard.CheckOnlyAdditions(original, newText);
        if (errors.Count > 0) return errors;

        var compileErrors = await CompilationChecker.GetErrorsAsync(ctx.TestProject, ctx.Item.TargetTestFile, newText, ct);
        if (compileErrors.Count > 0) return compileErrors;

        Directory.CreateDirectory(Path.GetDirectoryName(ctx.WorktreeTestFile)!);
        await File.WriteAllTextAsync(ctx.WorktreeTestFile, newText, ct);

        logger.LogInformation("{Id}: building {Project}", ctx.Item.Id, ctx.TestProjectPath);
        var build = await dotnet.BuildAsync(ctx.TestProjectPath, ct);
        if (!build.Success) return Errors(build);
        logger.LogInformation("{Id}: testing {Project}", ctx.Item.Id, ctx.TestProjectPath);
        var test = await dotnet.TestAsync(ctx.TestProjectPath, options.TestTimeout, ct, noBuild: true);
        return test.Success ? [] : Errors(test);
    }

    static IReadOnlyList<string> Errors(DotnetResult r) =>
        r.ErrorLines.Count > 0 ? r.ErrorLines : r.Output.Split('\n').TakeLast(20).ToList();

    static bool IsUnmockable(CtorParam p)
    {
        if (p.IsInterface || p.IsAbstract) return false;
        var simple = p.TypeFullName.Split('<')[0].Split('.')[^1];
        return UnmockableSuffixes.Any(s => simple.EndsWith(s, StringComparison.Ordinal))
               || p.TypeFullName.StartsWith("System.IO.", StringComparison.Ordinal)
               || simple == "Socket";
    }

    /// <summary>A test project at the repo root (<c>.</c>) contains every path, so nothing can be outside it.</summary>
    public static IReadOnlyList<string> OutsideTestProjects(IReadOnlyList<string> changed, IReadOnlyList<string> testDirsRelativeToRepo) =>
        testDirsRelativeToRepo.Contains(".") ? [] : QualityGuard.CheckOnlyTestProjectsTouched(changed, testDirsRelativeToRepo);

    public const string StatusArgs = "status --porcelain -z";

    // ponytail: bin/obj are ignored here (and never committed) for repos lacking a .gitignore for build output.
    /// <summary><c>-z</c>: NUL-separated, paths unquoted/unescaped; a rename/copy entry is followed by its source path.</summary>
    static async Task<IReadOnlyList<string>> ChangedFilesAsync(IGitRunner worktree, CancellationToken ct)
    {
        var entries = (await worktree.RunAsync(StatusArgs, ct)).StdOut.Split('\0');
        var paths = new List<string>();
        for (var i = 0; i < entries.Length; i++)
        {
            var e = entries[i];
            if (e.Length <= 3) continue;
            paths.Add(e[3..]);
            if (e[0] is 'R' or 'C' || e[1] is 'R' or 'C') i++; // skip the source path
        }
        return paths.Where(p => !p.Replace('\\', '/').Split('/').Any(s => s is "bin" or "obj")).ToList();
    }

    async Task CleanupAsync(string wtDir, string branch, CancellationToken ct)
    {
        // Prune first: a registered worktree whose dir is gone makes `branch -D` refuse.
        await repoGit.WorktreePruneAsync(ct);
        if (Directory.Exists(wtDir))
        {
            logger.LogInformation("Removing leftover worktree {Dir}", wtDir);
            try { await repoGit.WorktreeRemoveAsync(wtDir, force: true, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning("Removing leftover worktree {Dir} failed: {Message}", wtDir, ex.Message); }
        }
        if (await repoGit.BranchExistsAsync(branch, ct))
        {
            logger.LogInformation("Deleting leftover branch {Branch}", branch);
            try { await repoGit.DeleteBranchAsync(branch, force: true, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning("Deleting leftover branch {Branch} failed: {Message}", branch, ex.Message); }
        }
    }

    async Task<WorkResult> FailAsync(Run run, string wtDir, string branch, string reason)
    {
        logger.LogInformation("{Id}: failed: {Reason}", run.Id, reason);
        await RollbackAsync(run.Id, wtDir, branch);
        return run.Result(WorkStatus.Failed, reason);
    }

    async Task RollbackAsync(string id, string wtDir, string branch)
    {
        logger.LogInformation("{Id}: rolling back", id);
        try { await repoGit.WorktreeRemoveAsync(wtDir, force: true, CancellationToken.None); }
        catch (Exception ex) { logger.LogWarning(ex, "{Id}: worktree remove failed", id); }
        try { await repoGit.WorktreePruneAsync(CancellationToken.None); }
        catch (Exception ex) { logger.LogWarning(ex, "{Id}: worktree prune failed", id); }
        try { await repoGit.DeleteBranchAsync(branch, force: true, CancellationToken.None); }
        catch (Exception ex) { logger.LogWarning(ex, "{Id}: branch delete failed", id); }
    }

    /// <summary>Path relative to <paramref name="repoRoot"/> (which git reports symlink-resolved); retries with the real path when a symlink makes it escape.</summary>
    public static string RepoRelative(string repoRoot, string path)
    {
        var rel = Path.GetRelativePath(repoRoot, path);
        return rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel)
            ? Path.GetRelativePath(RealPath(repoRoot), RealPath(path))
            : rel;
    }

    /// <summary>Resolves symlinks in every existing component of <paramref name="path"/>.</summary>
    static string RealPath(string path)
    {
        var full = Path.GetFullPath(path);
        var current = Path.GetPathRoot(full)!;
        foreach (var part in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            var info = new FileInfo(current);
            if (info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target) current = target.FullName;
        }
        return current;
    }

    static bool SamePath(string? a, string? b) =>
        a is not null && b is not null && Path.GetFullPath(a) == Path.GetFullPath(b);
}
