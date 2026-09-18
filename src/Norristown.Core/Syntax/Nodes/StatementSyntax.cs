using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>
/// What a line parses to: a declaration, a directive, an instruction, a member of a body.
/// A few of them are also written inside another statement, as an instruction is after a
/// label and a declaration after <c>.export</c>; the line break then belongs to the outer one.
/// </summary>
public abstract class StatementSyntax : SyntaxNode
{
    private protected StatementSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The line break that ends the statement's line, or null for a statement written inside another.</summary>
    public SyntaxToken? EndOfLineToken => FirstToken(SyntaxKind.EndOfLine);

    /// <summary>What was left on the line that the statement could not take, or null.</summary>
    public SkippedTokensSyntax? SkippedTokens => FirstNode<SkippedTokensSyntax>();

    /// <summary>Whether this is a declaration written after <c>.export</c>.</summary>
    public bool IsExported => Parent is ExportedDeclarationSyntax;

    /// <summary>The <c>.export</c> a declaration is written after, or null.</summary>
    public SyntaxToken? ExportToken => (Parent as ExportedDeclarationSyntax)?.ExportKeyword;
}
