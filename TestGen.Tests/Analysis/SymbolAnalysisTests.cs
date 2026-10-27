using Microsoft.CodeAnalysis;
using Shouldly;
using TestGen.Analysis;
using Xunit;
using static TestGen.Tests.Analysis.RoslynTestWorkspace;

namespace TestGen.Tests.Analysis;

public class SymbolAnalysisTests
{
    static async Task<INamedTypeSymbol> Calc(Solution s) =>
        (await TypeLocator.FindTypeAsync(s, "Acme.Core.Services.Calc", CancellationToken.None))!;

    [Fact]
    public async Task FindTypeAsync_NestedAndGeneric_Resolves()
    {
        var s = Create(Samples.Prod);
        (await TypeLocator.FindTypeAsync(s, "Acme.Core.Services.Calc/Inner", default))!.Name.ShouldBe("Inner");
        var box = await TypeLocator.FindTypeAsync(s, "Acme.Core.Services.Box`1", default);
        box!.Arity.ShouldBe(1);
        (await TypeLocator.FindTypeAsync(s, "Acme.Core.Services.Calc/<Run>d__1", default))!.Name.ShouldBe("Calc");
        (await TypeLocator.FindTypeAsync(s, "Nope.Missing", default)).ShouldBeNull();
    }

    [Fact]
    public async Task FindMethod_Overloads_MatchedBySignature()
    {
        var calc = await Calc(Create(Samples.Prod));
        TypeLocator.FindMethod(calc, "Add", "(System.String,System.Int32)")!.ParameterList.Parameters[0].Type!.ToString().ShouldBe("string");
        TypeLocator.FindMethod(calc, "Add", "(System.Int32,System.Int32)")!.ParameterList.Parameters[0].Type!.ToString().ShouldBe("int");
        TypeLocator.FindMethod(calc, "Missing", null).ShouldBeNull();

        var (start, end) = TypeLocator.LineSpan(TypeLocator.FindMethod(calc, "Run", null)!);
        start.ShouldBe(23);
        end.ShouldBe(23);
    }

    [Fact]
    public async Task FullSource_HasUsingsNamespaceAndType()
    {
        var calc = await Calc(Create(Samples.Prod));
        TypeLocator.Declarations(calc).Count.ShouldBe(1);
        var src = TypeLocator.FullSource(calc);
        src.ShouldStartWith("using System;");
        src.ShouldContain("namespace Acme.Core.Services;");
        src.ShouldContain("public class Calc");
        src.ShouldNotContain("class Box");
    }

    [Fact]
    public void TrimMethodBodies_KeepsNamedMethods_StubsOthers()
    {
        var trimmed = SourceTrimmer.TrimMethodBodies(Samples.Calc, new HashSet<string> { "Helper" });
        trimmed.ShouldContain("private int Helper(int x) => Deep(x) + 1;");
        trimmed.ShouldContain("public int Run(int x) { /* ... */ }");
        trimmed.ShouldNotContain("return a + b");
        trimmed.ShouldNotContain("return _clock.Now");
        trimmed.ShouldContain("public string Add(string a, int b)");
        trimmed.ShouldContain("public T? Value { get; set; }");
    }

    [Fact]
    public async Task Collect_ListsCtorParamsAndMockableMembers()
    {
        var deps = DependencyCollector.Collect(await Calc(Create(Samples.Prod)));
        deps.CtorParams.Select(p => p.Name).ShouldBe(["clock", "notifier", "seed"]);
        deps.CtorParams[0].ShouldBe(new CtorParam("clock", "Acme.Core.Services.IClock", true, false));
        deps.CtorParams[1].IsAbstract.ShouldBeTrue();
        deps.CtorParams[2].IsInterface.ShouldBeFalse();
        deps.MemberSignatures.ShouldContain("IClock: DateTime Now { get; }");
        deps.MemberSignatures.ShouldContain("Notifier: void Notify(string msg)");
        deps.MemberSignatures.ShouldNotContain(s => s.Contains("Log"));
    }

    [Fact]
    public async Task FindPathsAsync_PrivateMethod_PathFromPublicEntry()
    {
        var s = Create(Samples.Prod);
        var deep = (IMethodSymbol)(await Calc(s)).GetMembers("Deep").Single();
        var paths = await CallPathFinder.FindPathsAsync(s, deep, ct: default);
        paths.ShouldBe(["Acme.Core.Services.Calc.Run(int) -> Acme.Core.Services.Calc.Helper(int) -> Acme.Core.Services.Calc.Deep(int)"]);
    }

    [Fact]
    public void IsTestProject_ByNameAndByReference()
    {
        var s = Create(Samples.Prod, [("A.cs", "class A {}")]);
        SolutionLoader.IsTestProject(Project(s, "Acme.Core.Tests")).ShouldBeTrue();
        SolutionLoader.IsTestProject(Project(s, "Acme.Core")).ShouldBeFalse();
        SolutionLoader.ProductionProjects(s).Single().Name.ShouldBe("Acme.Core");

        var byRef = Create(Samples.Prod, [("A.cs", "class A {}")], testName: "Acme.Checks");
        SolutionLoader.TestProjects(byRef).Single().Name.ShouldBe("Acme.Checks");
        var noRef = Create(Samples.Prod, [("A.cs", "class A {}")], testName: "Acme.Checks", withTestLibraries: false);
        SolutionLoader.TestProjects(noRef).ShouldBeEmpty();
    }
}
