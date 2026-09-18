using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>name(args)</c> or <c>.function(args)</c>.</summary>
public sealed class CallExpressionSyntax : ExpressionSyntax
{
    internal CallExpressionSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The <c>.func</c> called, or null for a built-in function.</summary>
    public NameExpressionSyntax? Callee => ChildNodes[0] as NameExpressionSyntax;

    /// <summary>The built-in function called, or null for a <c>.func</c>.</summary>
    public SyntaxToken? Function => TokenAt(0);

    /// <summary>The arguments.</summary>
    public ArgumentListSyntax Arguments => FirstNode<ArgumentListSyntax>()!;
}
