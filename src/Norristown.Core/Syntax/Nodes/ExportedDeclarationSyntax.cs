using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>.export</c> and the declaration written after it. A line's statement is the declaration itself, so this is met only as its parent.</summary>
public sealed class ExportedDeclarationSyntax : StatementSyntax
{
    internal ExportedDeclarationSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The <c>.export</c>.</summary>
    public SyntaxToken ExportKeyword => ChildTokens[0];

    /// <summary>The declaration exported.</summary>
    public StatementSyntax Declaration => (StatementSyntax)ChildNodes[0];
}
