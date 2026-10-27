using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace TestGen.Analysis;

public static class SourceTrimmer
{
    public static string TrimMethodBodies(string classSource, IReadOnlySet<string> keepMethodNames) =>
        new Rewriter(keepMethodNames).Visit(CSharpSyntaxTree.ParseText(classSource).GetRoot()).ToFullString();

    static BlockSyntax Stub() =>
        Block().WithOpenBraceToken(Token(TriviaList(), SyntaxKind.OpenBraceToken, TriviaList(Space, Comment("/* ... */"), Space)));

    sealed class Rewriter(IReadOnlySet<string> keep) : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node) =>
            keep.Contains(node.Identifier.Text) || (node.Body is null && node.ExpressionBody is null)
                ? node
                : node.WithExpressionBody(null).WithSemicolonToken(default)
                    .WithBody(Stub().WithTrailingTrivia(node.GetTrailingTrivia()));

        public override SyntaxNode? VisitAccessorDeclaration(AccessorDeclarationSyntax node)
        {
            var owner = node.Parent?.Parent switch
            {
                PropertyDeclarationSyntax p => p.Identifier.Text,
                EventDeclarationSyntax e => e.Identifier.Text,
                _ => null,
            };
            if ((owner is not null && keep.Contains(owner)) || (node.Body is null && node.ExpressionBody is null)) return node;
            return node.WithExpressionBody(null).WithSemicolonToken(default).WithBody(Stub());
        }
    }
}
