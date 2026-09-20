using System.Collections.Immutable;
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>
/// One source line, in the pieces it is written in: the <c>.export</c> that exports what it
/// declares, its <see cref="Statement"/>, whatever the statement could not take, and the line
/// break that ends it. Those pieces are the line's children, so a walk of the tree meets each
/// token once and under the node it is part of; <see cref="Tokens"/> is the same line read as the
/// lexer read it, as a flat run of tokens belonging to the line.
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
    /// null. It belongs to the line rather than to the declaration, as the line break does, so
    /// the declaration reads as the same one written without it;
    /// <see cref="StatementSyntax.IsExported"/> says the <c>.export</c> is there.
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
    /// The line's tokens as the lexer read them, the line break last. They belong to the line,
    /// and the same tokens are held again by the pieces the line is written in, where they belong
    /// to the node each is part of; the pieces hold a missing token as well, which the source does
    /// not write and the lexer never read.
    /// <para>
    /// The list is a view over the line, so reading a whole file's tokens through it neither
    /// allocates nor makes a statement: it is the line as the lexer left it.
    /// </para>
    /// </summary>
    public SyntaxTokenList Tokens => new(this);

    /// <inheritdoc/>
    /// <remarks>
    /// A green line holds the tokens the lexer read and not the pieces they parse to, so the
    /// line's own answer is both of theirs, and the tree works it out for every line as it is
    /// built rather than each line working it out again.
    /// </remarks>
    public override bool ContainsDiagnostics => Tree.LinesContainDiagnostics(LineIndex, LineIndex);

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
