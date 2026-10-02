using System.Collections.Immutable;
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>
/// Represents a block, which consists of its opening line, its contents, and the closing
/// <c>}</c> line if it has one. A block closed by a continuation line (<c>} .else {</c>) has no
/// closing line of its own, because that line opens the next block.
/// </summary>
public sealed partial class BlockSyntax : SyntaxNode
{
    internal BlockSyntax(SyntaxTree tree, SyntaxNode? parent, GreenBlock green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>Gets the kind of block, which the statement at the end of its opening line determines.</summary>
    public BlockKind BlockKind => GreenBlock.BlockKind;

    /// <summary>Gets a value indicating whether the block ends with a <c>}</c> line of its own.</summary>
    public bool HasCloser => GreenBlock.HasCloser;

    /// <summary>Gets the line that opens the block.</summary>
    public LineSyntax Opener => (LineSyntax)ChildNodes[0];

    /// <summary>Gets the closing <c>}</c> line, or null if a continuation line ends the block.</summary>
    public LineSyntax? Closer => HasCloser ? (LineSyntax)ChildNodes[^1] : null;

    /// <summary>
    /// Gets the opening line, the block's contents, and the closing line if the block has one.
    /// </summary>
    public ImmutableArray<SyntaxNode> Members => ChildNodes;

    /// <inheritdoc/>
    /// <remarks>
    /// A block's children are its source lines rather than the statements they parse to, so the
    /// value comes from the tree's per-line record for the block's lines. This is the same way
    /// <see cref="SyntaxNode.GetDiagnostics"/> collects them.
    /// </remarks>
    public override bool ContainsDiagnostics => Tree.LinesContainDiagnostics(LineIndex, LastLineIndex);

    /// <inheritdoc/>
    /// <remarks>
    /// The value comes from the tree's per-line record for the block's lines, as it does for the
    /// diagnostics, and from the tree's record of the annotations of the block and the blocks and
    /// lines inside it.
    /// </remarks>
    public override bool ContainsAnnotations =>
        Tree.LinesContainAnnotations(LineIndex, LastLineIndex) || Tree.NodesContainAnnotations(LineIndex, LastLineIndex);

    private GreenBlock GreenBlock => (GreenBlock)Green;

    /// <summary>
    /// Returns the item block that the line holding <paramref name="directive"/> opens, or null
    /// when the line opens none. <see cref="SyntaxFacts.IsItemBlock"/> describes item blocks.
    /// </summary>
    internal static BlockSyntax? ItemBlockOf(StatementSyntax directive) =>
        directive.FirstAncestorOrSelf<LineSyntax>() is { Parent: BlockSyntax block } line
            && block.Opener == line && SyntaxFacts.IsItemBlock(block.BlockKind)
            ? block
            : null;

    /// <summary>
    /// Returns the directive whose item block holds <paramref name="line"/>, or null when no item
    /// block holds it or its directive is not a <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">The kind of directive.</typeparam>
    /// <param name="line">The statement of one line of the block.</param>
    internal static T? ItemDirectiveOf<T>(StatementSyntax line) where T : StatementSyntax =>
        line.Parent?.Parent is BlockSyntax block && SyntaxFacts.IsItemBlock(block.BlockKind)
            ? block.Opener.Statement as T ?? (block.Opener.Statement as LabeledLineSyntax)?.Statement as T
            : null;

    /// <summary>
    /// Returns every item a list directive gives, which are the items on its own line followed by
    /// those on the lines of the item block it opens.
    /// </summary>
    /// <typeparam name="TLine">The statement each line of the item block parses to.</typeparam>
    /// <typeparam name="TItem">The type of one item.</typeparam>
    /// <param name="directive">The directive.</param>
    /// <param name="inline">The items on the directive's own line.</param>
    /// <param name="itemsOf">Returns the items on one line of the block.</param>
    internal static IReadOnlyList<TItem> ItemsOf<TLine, TItem>(
        StatementSyntax directive, SeparatedSyntaxList<TItem> inline, Func<TLine, SeparatedSyntaxList<TItem>> itemsOf)
        where TLine : StatementSyntax
        where TItem : SyntaxNode
    {
        if (ItemBlockOf(directive) is not { } block)
            return inline;
        var items = new List<TItem>(inline);
        foreach (var member in block.Members)
        {
            if (member is LineSyntax { Statement: TLine line })
                items.AddRange(itemsOf(line));
        }
        return items;
    }

    /// <inheritdoc/>
    private protected override void CollectDiagnostics(List<Diagnostic> result) =>
        Tree.CollectLines(LineIndex, LastLineIndex, result);
}
