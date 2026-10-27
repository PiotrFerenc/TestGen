using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TestGen.Analysis;

/// <summary>Syntax-only helpers shared by convention detection, snippet validation and quality guards.</summary>
internal static class TestSyntax
{
    public static readonly HashSet<string> TestAttributes = ["Fact", "Theory", "Test", "TestCase", "TestCaseSource", "TestMethod", "DataTestMethod"];

    public static string AttributeName(AttributeSyntax a)
    {
        var name = a.Name switch
        {
            QualifiedNameSyntax q => q.Right.Identifier.Text,
            AliasQualifiedNameSyntax q => q.Name.Identifier.Text,
            SimpleNameSyntax s => s.Identifier.Text,
            _ => a.Name.ToString(),
        };
        return name.EndsWith("Attribute", StringComparison.Ordinal) ? name[..^"Attribute".Length] : name;
    }

    public static IEnumerable<string> AttributeNames(MemberDeclarationSyntax m) =>
        m.AttributeLists.SelectMany(l => l.Attributes).Select(AttributeName);

    public static bool IsTestMethod(MethodDeclarationSyntax m) => AttributeNames(m).Any(TestAttributes.Contains);

    public static IEnumerable<MethodDeclarationSyntax> TestMethods(SyntaxNode root) =>
        root.DescendantNodes().OfType<MethodDeclarationSyntax>().Where(IsTestMethod);

    static bool IsAssertName(string n) =>
        n is "Assert" or "Should" or "Received" or "DidNotReceive" or "Verify" or "VerifyAll" or "Expect" or "Is" or "Has"
        || n.StartsWith("Should", StringComparison.Ordinal) || n.StartsWith("Throws", StringComparison.Ordinal)
        || n.StartsWith("Assert", StringComparison.Ordinal);

    static string? InvokedName(InvocationExpressionSyntax c) => c.Expression switch
    {
        MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
        SimpleNameSyntax s => s.Identifier.Text,
        _ => null,
    };

    static bool IsTrivialAssert(InvocationExpressionSyntax c) =>
        InvokedName(c) is "True" or "IsTrue" or "False" or "IsFalse" or "That"
        && c.ArgumentList.Arguments.Count == 1
        && c.ArgumentList.Arguments[0].Expression.Kind() is SyntaxKind.TrueLiteralExpression or SyntaxKind.FalseLiteralExpression;

    /// <summary>Errors for forbidden patterns in test methods; with requireAssertions also flags tests without an assertion.</summary>
    public static IEnumerable<string> Check(SyntaxNode root, bool requireAssertions)
    {
        foreach (var m in TestMethods(root))
        {
            var name = m.Identifier.Text;
            var attrs = m.AttributeLists.SelectMany(l => l.Attributes).ToList();
            if (attrs.Any(a => AttributeName(a) is "Skip" or "Ignore"
                               || a.ArgumentList?.Arguments.Any(x => (x.NameEquals?.Name.Identifier.Text ?? x.NameColon?.Name.Identifier.Text) == "Skip") == true))
                yield return $"{name}: skipped/ignored tests are forbidden";

            SyntaxNode? body = (SyntaxNode?)m.Body ?? m.ExpressionBody;
            if (body is null || m.Body is { Statements.Count: 0 })
            {
                yield return $"{name}: empty test body";
                continue;
            }

            var calls = body.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().ToList();
            if (calls.Any(IsTrivialAssert)) yield return $"{name}: trivial assertion like Assert.True(true) is forbidden";
            if (!requireAssertions) continue;

            if (!body.DescendantNodes().OfType<SimpleNameSyntax>().Any(n => IsAssertName(n.Identifier.Text)))
                yield return $"{name}: test has no assertion";
            else
            {
                var assertCalls = calls.Where(c => c.Expression.DescendantNodesAndSelf().OfType<SimpleNameSyntax>().Any(n => IsAssertName(n.Identifier.Text))).ToList();
                if (assertCalls.Count > 0 && assertCalls.All(c => InvokedName(c) == "Fail"))
                    yield return $"{name}: Assert.Fail is the only assertion";
            }
        }
    }
}
