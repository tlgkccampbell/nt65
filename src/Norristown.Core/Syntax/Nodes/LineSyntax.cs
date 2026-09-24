using System.Collections.Immutable;
using System.Diagnostics;
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>
/// Represents one source line, split into the pieces it is made of. These are an optional
/// <c>.export</c> that exports what the line declares, its <see cref="Statement"/>, any tokens the
/// statement could not consume, and the line break that ends it. Those pieces are the line's
/// children, so a walk of the tree reaches each token once, under the node it is part of.
/// <see cref="Tokens"/> gives the same line as the lexer read it, as a flat run of those same
/// tokens.
/// </summary>
public sealed partial class LineSyntax : SyntaxNode
{
    private StatementSyntax? statement;
    private SkippedTokensSyntax? skipped;
    private ImmutableArray<SyntaxNodeOrToken> pieces;
    private ImmutableArray<SyntaxToken> tokens;

    internal LineSyntax(SyntaxTree tree, SyntaxNode? parent, GreenLine green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>Gets the kind of this line, which its tokens alone determine.</summary>
    public LineKind LineKind => GreenLine.LineKind;

    /// <summary>
    /// Gets the kind of block this line opens. The value is <see cref="BlockKind.Region"/> for a
    /// <c>.segment NAME</c> region line, and <see cref="BlockKind.None"/> for a line that opens no
    /// block.
    /// </summary>
    public BlockKind OpensBlockKind => GreenLine.OpensBlockKind;

    /// <summary>
    /// Gets the <c>.export</c> before a declaration, which exports what the line declares, or null
    /// if there is none. Like the line break, it belongs to the line rather than to the
    /// declaration, so the declaration's node looks the same whether or not it is exported.
    /// <see cref="StatementSyntax.IsExported"/> indicates whether the <c>.export</c> is present.
    /// </summary>
    public SyntaxToken? ExportKeyword => Parsed.ExportKeyword is null ? null : SlotToken(0);

    /// <summary>Gets the statement that the line's own tokens parse to.</summary>
    public StatementSyntax Statement => statement ??= (StatementSyntax)Parsed.Node.CreateRed(
        Tree, this, Position + (Parsed.ExportKeyword?.FullWidth ?? 0));

    /// <summary>
    /// Gets the tokens left on the line that the statement could not consume, or null if there are
    /// none.
    /// </summary>
    public SkippedTokensSyntax? SkippedTokens =>
        skipped ??= Parsed.SkippedTokens is { } left
            ? (SkippedTokensSyntax)left.CreateRed(Tree, this, Statement.FullSpan.End)
            : null;

    /// <summary>Gets the line break that ends the line, which is the statement's terminator.</summary>
    public SyntaxToken EndOfLineToken => SlotToken(Green.SlotCount - 1);

    /// <summary>
    /// Gets the line's tokens as the lexer read them, with the line break last. They are the
    /// tokens that <see cref="SyntaxNode.DescendantTokens"/> reaches, each with the node it is part
    /// of as its parent, so a token read here equals the same token reached through
    /// <see cref="Statement"/>. The line's pieces can also hold missing tokens, which the source
    /// does not contain, so they do not appear here.
    /// </summary>
    public SyntaxTokenList Tokens => new(ChildTokens);

    /// <inheritdoc/>
    /// <remarks>
    /// A line's child tokens are the tokens the lexer read, the same as <see cref="Tokens"/>, so
    /// each has the node it is part of as its parent rather than the line.
    /// </remarks>
    public override ImmutableArray<SyntaxToken> ChildTokens
    {
        get
        {
            if (tokens.IsDefault)
                ImmutableInterlocked.InterlockedInitialize(ref tokens, ReadTokens());
            return tokens;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// A green line holds the tokens the lexer read and not the pieces they parse to, so the value
    /// has to cover both. The tree computes it once for every line as the tree is built, rather
    /// than each line computing it again.
    /// </remarks>
    public override bool ContainsDiagnostics => Tree.LinesContainDiagnostics(LineIndex, LineIndex);

    /// <inheritdoc/>
    /// <remarks>
    /// As with diagnostics, the green line holds the tokens the lexer read rather than the pieces
    /// they parse to. So the value covers both, and the tree computes it once per line.
    /// </remarks>
    public override bool ContainsAnnotations => Tree.LinesContainAnnotations(LineIndex, LineIndex);

    /// <inheritdoc/>
    internal override ImmutableArray<SyntaxNodeOrToken>? RedChildren
    {
        get
        {
            if (pieces.IsDefault)
            {
                var builder = ImmutableArray.CreateBuilder<SyntaxNodeOrToken>(4);
                if (ExportKeyword is { } exported)
                    builder.Add(exported);
                builder.Add(Statement);
                if (SkippedTokens is { } left)
                    builder.Add(left);
                builder.Add(EndOfLineToken);
                ImmutableInterlocked.InterlockedInitialize(ref pieces, builder.ToImmutable());
            }
            return pieces;
        }
    }

    private GreenLine GreenLine => (GreenLine)Green;

    private Parser.Result Parsed => Tree.Parsed(LineIndex);

    private protected override ImmutableArray<SyntaxNode> CreateChildNodes() =>
        SkippedTokens is { } left ? [Statement, left] : [Statement];

    /// <inheritdoc/>
    private protected override void CollectDiagnostics(List<Diagnostic> result) =>
        Tree.CollectLines(LineIndex, LineIndex, result);

    /// <summary>
    /// Returns the tokens of the line's pieces that the source contains, in source order. They are
    /// the line's slots, reached through the nodes they are part of.
    /// </summary>
    private ImmutableArray<SyntaxToken> ReadTokens()
    {
        var builder = ImmutableArray.CreateBuilder<SyntaxToken>(Green.SlotCount);
        foreach (var piece in RedChildren!.Value)
        {
            if (piece.AsNode() is not { } node)
            {
                builder.Add(piece.AsToken());
                continue;
            }
            foreach (var token in node.DescendantTokens())
            {
                if (!token.IsMissing)
                    builder.Add(token);
            }
        }
        Debug.Assert(builder.Count == Green.SlotCount, "a line's pieces hold exactly the tokens the lexer read");
        return builder.MoveToImmutable();
    }
}
