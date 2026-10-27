using Microsoft.CodeAnalysis.CSharp.Syntax;
using Shouldly;
using TestGen.Analysis;
using Xunit;
using static TestGen.Tests.Analysis.RoslynTestWorkspace;

namespace TestGen.Tests.Analysis;

public class SnippetPipelineTests
{
    static readonly TestConventions Conv = TestConventions.Default("xunit", "NSubstitute", "Shouldly");

    const string LooseSnippet = """
        using NSubstitute;
        using Xunit;

        private readonly int _seed = 1;

        [Fact]
        public void Run_Zero_ReturnsOne()
        {
            var calc = new Calc(Substitute.For<IClock>(), null!, _seed);
            Assert.Equal(1, calc.Run(0));
        }
        """;

    static CompilationUnitSyntax Parse(string snippet)
    {
        var r = SnippetValidator.Validate(snippet, Conv);
        r.Errors.ShouldBeEmpty();
        return r.Parsed!;
    }

    [Fact]
    public void Validate_LooseMembers_NormalizedToUsingsAndMembers()
    {
        var r = SnippetValidator.Validate(LooseSnippet, Conv);
        r.IsValid.ShouldBeTrue();
        r.Parsed!.Usings.Select(u => u.Name!.ToString()).ShouldBe(["NSubstitute", "Xunit"]);
        r.Parsed.Members.Count.ShouldBe(2);
        r.Parsed.Members[1].ShouldBeOfType<MethodDeclarationSyntax>().Identifier.Text.ShouldBe("Run_Zero_ReturnsOne");
    }

    [Fact]
    public void Validate_FencedWrappedClass_TakesClassMembers()
    {
        var r = SnippetValidator.Validate("Here you go:\n```csharp\nusing Xunit;\nnamespace X;\npublic class CalcTests\n{\n    [Fact]\n    public void A() { 1.ShouldBe(1); }\n}\n```\nthanks", Conv);
        r.IsValid.ShouldBeTrue();
        r.Parsed!.Members.Single().ShouldBeOfType<MethodDeclarationSyntax>().Identifier.Text.ShouldBe("A");
        r.Parsed.Usings.Single().Name!.ToString().ShouldBe("Xunit");
    }

    [Theory]
    [InlineData("[Fact(Skip = \"later\")] public void A() { Assert.Equal(1, 1); }", "skipped")]
    [InlineData("[Fact] public void A() { var x = 1; }", "no assertion")]
    [InlineData("[Fact] public void A() { Assert.True(true); }", "trivial assertion")]
    [InlineData("[Fact] public void A() { Assert.Fail(\"x\"); }", "Assert.Fail")]
    [InlineData("[Fact] public void A() { }", "empty test body")]
    [InlineData("public void Helper() { Assert.Equal(1, 1); }", "No test methods")]
    [InlineData("[Fact] public void A() { Assert.Equal(1, 1) }", "CS1002")]
    public void Validate_Rules_ReportErrors(string snippet, string expected)
    {
        var r = SnippetValidator.Validate(snippet, Conv);
        r.IsValid.ShouldBeFalse();
        r.Errors.ShouldContain(e => e.Contains(expected));
    }

    [Fact]
    public async Task InsertAsync_AppendsMembersAndUsings_KeepsExisting()
    {
        const string existing = """
            using Acme.Core.Services;
            using Xunit;

            namespace Acme.Core.Tests;

            public class CalcTests
            {
                [Fact]
                public void Add_Works()   {  Assert.Equal(3, new Calc(null!, null!, 1).Add(1, 2)); }
            }
            """;
        var s = Create(Samples.Prod, [("CalcTests.cs", existing)]);
        var result = await TestInserter.InsertAsync(Doc(s, "CalcTests.cs"), Parse(LooseSnippet), default);

        result.ShouldContain("public void Add_Works()   {  Assert.Equal"); // existing code untouched
        result.ShouldContain("\n    [Fact]\n    public void Run_Zero_ReturnsOne()\n    {\n        var calc"); // new code formatted
        result.ShouldContain("    private readonly int _seed = 1;");
        result.Split("using Xunit;").Length.ShouldBe(2);
        result.ShouldContain("using NSubstitute;");
        result.IndexOf("Run_Zero_ReturnsOne").ShouldBeGreaterThan(result.IndexOf("Add_Works"));
        QualityGuard.CheckOnlyAdditions(existing, result).ShouldBeEmpty();
        (await CompilationChecker.GetErrorsAsync(Project(s, "Acme.Core.Tests"), $"{TestDir()}/CalcTests.cs", result, default)).ShouldBeEmpty();
    }

    [Fact]
    public async Task InsertAsync_ClassName_TargetsNamedClassIncludingNested()
    {
        const string existing = """
            using Xunit;

            namespace Acme.Core.Tests;

            public class Helpers { }

            public class Outer
            {
                public class CalcTests { }
            }
            """;
        var s = Create(Samples.Prod, [("CalcTests.cs", existing)]);
        var result = await TestInserter.InsertAsync(Doc(s, "CalcTests.cs"), Parse(LooseSnippet), default, "CalcTests");
        result.IndexOf("Run_Zero_ReturnsOne").ShouldBeGreaterThan(result.IndexOf("class CalcTests"));
        result.ShouldContain("public class Helpers { }");

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => TestInserter.InsertAsync(Doc(s, "CalcTests.cs"), Parse(LooseSnippet), default, "Nope"));
        ex.Message.ShouldContain("'Nope' not found");
    }

    [Fact]
    public void Validate_RejectsSkipAndTrivialAssert()
    {
        var r = SnippetValidator.Validate("""
            [Fact(Skip = "x")] public void A() { Assert.Equal(1, 2); }
            [Fact] public void B() { Assert.True(true); }
            """, Conv);
        r.IsValid.ShouldBeFalse();
        r.Errors.ShouldContain(e => e.StartsWith("A:") && e.Contains("skipped"));
        r.Errors.ShouldContain(e => e.StartsWith("B:") && e.Contains("trivial assertion"));
    }

    [Fact]
    public void CreateNewFile_FileScopedOrBlockNamespace()
    {
        var snippet = Parse(LooseSnippet);
        var fileScoped = TestInserter.CreateNewFile("CalcTests", "Acme.Core.Tests.Services", Conv with { Usings = ["Xunit", "System", "Shouldly"] }, snippet);
        fileScoped.ShouldStartWith("using System;\nusing NSubstitute;\nusing Shouldly;\nusing Xunit;\n");
        fileScoped.ShouldContain("namespace Acme.Core.Tests.Services;");
        fileScoped.ShouldContain("public class CalcTests");
        fileScoped.ShouldContain("public void Run_Zero_ReturnsOne()");

        var block = TestInserter.CreateNewFile("CalcTests", "Acme.Core.Tests", Conv with { FileScopedNamespaces = false }, snippet);
        block.ShouldContain("namespace Acme.Core.Tests\n{");
        block.ShouldNotContain("namespace Acme.Core.Tests;");
    }

    [Fact]
    public void CheckOnlyAdditions_DetectsRemovalAndModification()
    {
        const string original = "using Xunit;\npublic class T\n{\n    private int _x;\n    [Fact] public void A() { Assert.Equal(1, 1); }\n    [Fact] public void B() { Assert.Equal(2, 2); }\n}";
        QualityGuard.CheckOnlyAdditions(original, original.Replace("[Fact] public void B()", "[Fact]\n    public void C() { Assert.Equal(3, 3); }\n    [Fact]   public   void B()")).ShouldBeEmpty();

        QualityGuard.CheckOnlyAdditions(original, original.Replace("[Fact] public void B() { Assert.Equal(2, 2); }", ""))
            .ShouldBe(["Removed or modified member: B()"]);
        QualityGuard.CheckOnlyAdditions(original, original.Replace("Assert.Equal(1, 1)", "Assert.Equal(1, 2)"))
            .ShouldBe(["Removed or modified member: A()"]);
        var errors = QualityGuard.CheckOnlyAdditions(original, original.Replace("using Xunit;", "").Replace("class T", "class U"));
        errors.ShouldContain("Removed using: using Xunit;");
        errors.ShouldContain("Removed or renamed type: T");
    }

    [Fact]
    public void CheckForbiddenPatterns_FindsSkipTrivialAndEmpty()
    {
        var errors = QualityGuard.CheckForbiddenPatterns("""
            public class T
            {
                [Fact(Skip = "x")] public void A() { Assert.Equal(1, 2); }
                [Fact] public void B() { Assert.True(true); }
                [Fact] public void C() { }
                [Fact] public void D() { Assert.Equal(1, 1); }
            }
            """);
        errors.Count.ShouldBe(3);
        errors.ShouldContain(e => e.StartsWith("A:"));
        errors.ShouldContain(e => e.StartsWith("B:"));
        errors.ShouldContain(e => e.StartsWith("C:"));
    }

    [Fact]
    public void CheckOnlyTestProjectsTouched_FlagsProductionFiles()
    {
        var errors = QualityGuard.CheckOnlyTestProjectsTouched(
            ["tests/Acme.Core.Tests/CalcTests.cs", "tests\\Acme.Core.Tests\\Sub\\X.cs", "src/Acme.Core/Calc.cs", "tests/Acme.Core.TestsX/A.cs"],
            ["tests/Acme.Core.Tests/"]);
        errors.ShouldBe(["File outside test projects changed: src/Acme.Core/Calc.cs", "File outside test projects changed: tests/Acme.Core.TestsX/A.cs"]);
    }

    [Theory]
    [InlineData(".")]
    [InlineData("./")]
    [InlineData("")]
    public void CheckOnlyTestProjectsTouched_RepoRootProject_MatchesAll(string dir) =>
        QualityGuard.CheckOnlyTestProjectsTouched(["Calc.cs", "src/A.cs", ".config/x.json"], [dir]).ShouldBeEmpty();

    [Fact]
    public void CheckOnlyTestProjectsTouched_DottedDirKeptIntact()
    {
        QualityGuard.CheckOnlyTestProjectsTouched(["./.config/x.json", "config/y.json"], [".config"])
            .ShouldBe(["File outside test projects changed: config/y.json"]);
    }

    [Fact]
    public async Task GetErrorsAsync_UndefinedSymbolReported_ValidIsEmpty()
    {
        var s = Create(Samples.Prod, [("Existing.cs", "namespace Acme.Core.Tests; public class Existing { }")]);
        var project = Project(s, "Acme.Core.Tests");
        const string valid = "using Acme.Core.Services;\nusing Xunit;\nnamespace Acme.Core.Tests;\npublic class N { [Fact] public void A() { Assert.Equal(2, new Calc(null!, null!, 1).Add(1, 1)); } }";

        (await CompilationChecker.GetErrorsAsync(project, $"{TestDir()}/New.cs", valid, default)).ShouldBeEmpty();
        var errors = await CompilationChecker.GetErrorsAsync(project, $"{TestDir()}/Existing.cs", "public class Existing { void A() { Undefined.Call(); } }", default);
        errors.ShouldHaveSingleItem().ShouldStartWith("CS0103:");
        errors[0].ShouldEndWith("(line 1)");
    }
}
