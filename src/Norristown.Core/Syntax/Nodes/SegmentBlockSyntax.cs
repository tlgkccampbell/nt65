using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>.segment NAME {</c>: the line that opens a segment block.</summary>
public sealed class SegmentBlockSyntax : SegmentStatementSyntax
{
    internal SegmentBlockSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The <c>{</c> that opens the block, or null when it is not written.</summary>
    public SyntaxToken? OpenBraceToken => FirstToken(SyntaxKind.OpenBrace);
}
