using System.Collections.Immutable;
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>
/// One source line, in the pieces it is written in: the <c>.export</c> that exports what it
/// declares, its <see cref="Statement"/>, whatever the statement could not take, and the line
/// break that ends it. Its tokens are reachable both directly and through those pieces, which
/// between them hold the same tokens in the same order.
/// </summary>
public sealed class LineSyntax : SyntaxNode
{
    private StatementSyntax? statement;
    private SkippedTokensSyntax? skipped;

    internal LineSyntax(SyntaxTree tree, SyntaxNode? parent, GreenLine green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The green line this one wraps.</summary>
    public new GreenLine Green => (GreenLine)base.Green;

    /// <summary>What kind of line this is, from its tokens alone.</summary>
    public LineKind LineKind => Green.LineKind;

    /// <summary>
    /// For a line that opens a block, the kind of block; <see cref="BlockKind.Region"/> for a
    /// <c>.segment NAME</c> region line; otherwise <see cref="BlockKind.None"/>.
    /// </summary>
    public BlockKind OpensBlockKind => Green.OpensBlockKind;

    /// <summary>
    /// The <c>.export</c> written before a declaration, which exports what the line declares, or
    /// null. It belongs to the line rather than to the declaration, as the line break does, so
    /// the declaration reads as the same one written without it;
    /// <see cref="StatementSyntax.IsExported"/> says the <c>.export</c> is there.
    /// </summary>
    public SyntaxToken? ExportKeyword => Parsed.ExportKeyword is null ? null : ChildTokens[0];

    /// <summary>What the line's own tokens parse to.</summary>
    public StatementSyntax Statement => statement ??= (StatementSyntax)Parsed.Node.CreateRed(
        Tree, this, Position + (Parsed.ExportKeyword?.FullWidth ?? 0));

    /// <summary>What was left on the line that the statement could not take, or null.</summary>
    public SkippedTokensSyntax? SkippedTokens =>
        skipped ??= Parsed.SkippedTokens is { } left
            ? (SkippedTokensSyntax)left.CreateRed(Tree, this, Statement.FullSpan.End)
            : null;

    /// <summary>The line break that ends the line, which is the statement's terminator.</summary>
    public SyntaxToken EndOfLineToken => ChildTokens[^1];

    private Parser.Result Parsed => Tree.Parsed(LineIndex);

    private protected override ImmutableArray<SyntaxNode> CreateChildNodes() =>
        SkippedTokens is { } left ? [Statement, left] : [Statement];
}
