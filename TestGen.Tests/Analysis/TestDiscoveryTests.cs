using Microsoft.CodeAnalysis;
using Shouldly;
using TestGen.Analysis;
using Xunit;
using static TestGen.Tests.Analysis.RoslynTestWorkspace;

namespace TestGen.Tests.Analysis;

public class TestDiscoveryTests
{
    static async Task<TestTarget?> Locate(Solution s) =>
        await TestLocator.FindAsync(s, (await TypeLocator.FindTypeAsync(s, "Acme.Core.Services.Calc", default))!, default);

    [Fact]
    public async Task FindAsync_ByName()
    {
        var t = await Locate(Create(Samples.Prod, [("Services/CalcTests.cs", Samples.XunitTests)]));
        t.ShouldNotBeNull();
        t.Exists.ShouldBeTrue();
        t.TestClassName.ShouldBe("CalcTests");
        t.FilePath.ShouldBe($"{TestDir()}/Services/CalcTests.cs");
        t.Namespace.ShouldBe("Acme.Core.Tests.Services");
    }

    [Fact]
    public async Task FindAsync_ByReferences()
    {
        const string checks = """
            using Acme.Core.Services;
            namespace Acme.Core.Tests;
            public class Other { public void A() { } }
            public class MathChecks { public void A() { var c = new Calc(null!, null!, 1); Calc? d = c; } }
            """;
        var t = await Locate(Create(Samples.Prod, [("Unrelated.cs", "namespace Acme.Core.Tests; public class Unrelated {}"), ("MathChecks.cs", checks)]));
        t!.Exists.ShouldBeTrue();
        t.FilePath.ShouldBe($"{TestDir()}/MathChecks.cs");
        t.TestClassName.ShouldBe("MathChecks");
        t.Namespace.ShouldBe("Acme.Core.Tests");
    }

    [Fact]
    public async Task FindAsync_ProposesMirroredPathAndNamespace()
    {
        var t = await Locate(Create(Samples.Prod, [("Other.cs", "namespace Acme.Core.Tests; public class Other {}")]));
        t!.Exists.ShouldBeFalse();
        t.TestClassName.ShouldBeNull();
        t.TestProject.Name.ShouldBe("Acme.Core.Tests");
        t.FilePath.ShouldBe($"{TestDir()}/Services/CalcTests.cs");
        t.Namespace.ShouldBe("Acme.Core.Tests.Services");
    }

    [Fact]
    public async Task FindAsync_NeighboursUseProductionNamespaces_MirrorsThem()
    {
        var t = await Locate(Create(Samples.Prod, [("Other.cs", "namespace Acme.Core.Misc; public class OtherTests {}")]));
        t!.Namespace.ShouldBe("Acme.Core.Services");
    }

    [Fact]
    public async Task FindAsync_NoTestProject_ReturnsNull()
    {
        (await Locate(Create(Samples.Prod))).ShouldBeNull();
        (await Locate(Create(Samples.Prod, [("A.cs", "class A {}")], testName: "Unrelated.Tests"))).ShouldNotBeNull(); // the only test project
    }

    [Fact]
    public async Task DetectAsync_XunitNSubstituteShouldly()
    {
        var c = await ConventionDetector.DetectAsync(Create(Samples.Prod, [("Services/CalcTests.cs", Samples.XunitTests)]), default);
        c.Framework.ShouldBe("xunit");
        c.Mocking.ShouldBe("NSubstitute");
        c.Assertions.ShouldBe("Shouldly");
        c.UsesAutoFixture.ShouldBeFalse();
        c.NamingPattern.ShouldBe("Method_Scenario_Expected");
        c.UsesAaaComments.ShouldBeTrue();
        c.SetupStyle.ShouldBe("Constructor");
        c.UsingsOrder.ShouldBe("SystemFirst");
        c.FileScopedNamespaces.ShouldBeTrue();
        c.Usings.ShouldBe(["System", "NSubstitute", "Shouldly", "Xunit"]);
        c.SampleTestMethod!.ShouldContain("[Fact]");
        c.Summarize().ShouldContain("Test framework: xunit");
        c.Summarize().ShouldContain("Example test:");
    }

    [Fact]
    public async Task DetectAsync_NunitMoqFluentAssertions()
    {
        var c = await ConventionDetector.DetectAsync(Create(Samples.Prod, [("CalcTests.cs", Samples.NunitTests)]), default);
        c.Framework.ShouldBe("nunit");
        c.Mocking.ShouldBe("Moq");
        c.Assertions.ShouldBe("FluentAssertions");
        c.NamingPattern.ShouldBe("MethodName_Should_X");
        c.UsesAaaComments.ShouldBeFalse();
        c.SetupStyle.ShouldBe("SetUp");
        c.UsingsOrder.ShouldBe("Alphabetical");
        c.FileScopedNamespaces.ShouldBeFalse();
        c.Summarize().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Default_MapsLibrariesToUsings()
    {
        var c = TestConventions.Default("xunit", "NSubstitute", "Shouldly");
        c.Usings.ShouldBe(["Xunit", "NSubstitute", "Shouldly"]);
        c.Summarize().ShouldContain("Mocking library: NSubstitute");
    }
}
