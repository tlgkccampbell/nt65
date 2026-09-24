using System.Collections.Immutable;
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>Represents a whole file, which consists of the lines and blocks at its top level.</summary>
public sealed partial class FileSyntax : SyntaxNode
{
    private ImmutableArray<LineSyntax> lines;

    internal FileSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>Gets the file's top-level lines and blocks, in source order.</summary>
    public ImmutableArray<SyntaxNode> Members => ChildNodes;

    /// <inheritdoc/>
    /// <remarks>
    /// The root covers the whole file, so this value is true exactly when
    /// <see cref="SyntaxTree.Diagnostics"/> contains a syntax diagnostic.
    /// </remarks>
    public override bool ContainsDiagnostics => Tree.LinesContainDiagnostics(0, Tree.LineCount - 1);

    /// <inheritdoc/>
    /// <remarks>The root covers every line of the file, as it does for diagnostics.</remarks>
    public override bool ContainsAnnotations => Tree.LinesContainAnnotations(0, Tree.LineCount - 1);

    /// <summary>
    /// Gets the file's lines in source order, at any depth of block nesting. The blocks are walked
    /// once, on first access, so that finding the line at a position costs a lookup rather than a
    /// walk of the file.
    /// </summary>
    internal ImmutableArray<LineSyntax> Lines
    {
        get
        {
            if (lines.IsDefault)
            {
                var builder = ImmutableArray.CreateBuilder<LineSyntax>(Tree.LineStarts.Length);
                Collect(this, builder);
                ImmutableInterlocked.InterlockedInitialize(ref lines, builder.ToImmutable());
            }
            return lines;
        }
    }

    /// <inheritdoc/>
    private protected override void CollectDiagnostics(List<Diagnostic> result) =>
        Tree.CollectLines(0, Tree.LineCount - 1, result);

    /// <summary>
    /// Adds the lines under <paramref name="node"/>, including those under its blocks, in order.
    /// </summary>
    private static void Collect(SyntaxNode node, ImmutableArray<LineSyntax>.Builder builder)
    {
        foreach (var child in node.ChildNodes)
        {
            if (child is LineSyntax line)
                builder.Add(line);
            else if (child is BlockSyntax block)
                Collect(block, builder);
        }
    }
}
