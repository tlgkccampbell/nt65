using System.Collections.Immutable;
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>
/// One source line, split into the pieces it is made of: an optional <c>.export</c> that
/// exports what the line declares, its <see cref="Statement"/>, any tokens the statement could
/// not consume, and the line break that ends it. Those pieces are the line's children, so a walk
/// of the tree reaches each token once, under the node it is part of; <see cref="Tokens"/> gives
/// the same line as the lexer read it, as a flat run of tokens whose parent is the line.
/// </summary>
public sealed partial class LineSyntax : SyntaxNode
{
    private StatementSyntax? statement;
    private SkippedTokensSyntax? skipped;
    private ImmutableArray<SyntaxNodeOrToken> pieces;

    internal LineSyntax(SyntaxTree tree, SyntaxNode? parent, GreenLine green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>What kind of line this is, from its tokens alone.</summary>
    public LineKind LineKind => GreenLine.LineKind;

    /// <summary>
    /// For a line that opens a block, the kind of block; <see cref="BlockKind.Region"/> for a
    /// <c>.segment NAME</c> region line; otherwise <see cref="BlockKind.None"/>.
    /// </summary>
    public BlockKind OpensBlockKind => GreenLine.OpensBlockKind;

    /// <summary>
    /// The <c>.export</c> written before a declaration, which exports what the line declares, or
    /// null. Like the line break, it belongs to the line rather than to the declaration, so the
    /// declaration's node looks the same whether or not it is exported;
    /// <see cref="StatementSyntax.IsExported"/> says whether the <c>.export</c> is there.
    /// </summary>
    public SyntaxToken? ExportKeyword => Parsed.ExportKeyword is null ? null : Tokens[0];

    /// <summary>What the line's own tokens parse to.</summary>
    public StatementSyntax Statement => statement ??= (StatementSyntax)Parsed.Node.CreateRed(
        Tree, this, Position + (Parsed.ExportKeyword?.FullWidth ?? 0));

    /// <summary>What was left on the line that the statement could not take, or null.</summary>
    public SkippedTokensSyntax? SkippedTokens =>
        skipped ??= Parsed.SkippedTokens is { } left
            ? (SkippedTokensSyntax)left.CreateRed(Tree, this, Statement.FullSpan.End)
            : null;

    /// <summary>The line break that ends the line, which is the statement's terminator.</summary>
    public SyntaxToken EndOfLineToken => Tokens[^1];

    /// <summary>
    /// The line's tokens as the lexer read them, the line break last; their parent is the line.
    /// The same tokens are also held by the line's pieces, where each one's parent is the node it
    /// is part of. The pieces can also hold missing tokens, which the source does not contain and
    /// so do not appear here.
    /// <para>
    /// The list is a view over the line, so reading a whole file's tokens through it neither
    /// allocates nor parses any statement.
    /// </para>
    /// </summary>
    public SyntaxTokenList Tokens => new(this);

    /// <inheritdoc/>
    /// <remarks>
    /// A green line holds the tokens the lexer read and not the pieces they parse to, so the
    /// answer has to cover both. The tree computes it for every line once, as it is built, rather
    /// than each line computing it again.
    /// </remarks>
    public override bool ContainsDiagnostics => Tree.LinesContainDiagnostics(LineIndex, LineIndex);

    /// <inheritdoc/>
    /// <remarks>
    /// As with the diagnostics: the green line holds the tokens the lexer read rather than the
    /// pieces they parse to, so the answer covers both, and the tree computes it once per line.
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
}
