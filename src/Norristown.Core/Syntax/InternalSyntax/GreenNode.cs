using System.Collections.Immutable;
using System.Text;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// An immutable node with a kind, a width and children, and no parent or position, so an
/// unchanged line keeps its node across edits wherever the edit moved it.
/// </summary>
/// <param name="kind">The node's kind.</param>
/// <param name="fullWidth">The node's width, trivia included.</param>
public abstract class GreenNode(SyntaxKind kind, int fullWidth)
{
    /// <summary>What the node is.</summary>
    public SyntaxKind Kind { get; } = kind;

    /// <summary>Width including trivia.</summary>
    public int FullWidth { get; } = fullWidth;

    /// <summary>How many children the node has.</summary>
    public abstract int SlotCount { get; }

    /// <summary>The child at <paramref name="index"/>, from 0 to <see cref="SlotCount"/> − 1.</summary>
    public abstract GreenNode GetSlot(int index);

    /// <summary>The node's text, exactly as in the source.</summary>
    public string ToFullString()
    {
        var builder = new StringBuilder(FullWidth);
        WriteTo(builder);
        return builder.ToString();
    }

    /// <summary>The red node for this one at <paramref name="position"/> under <paramref name="parent"/>.</summary>
    internal abstract SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position);

    internal virtual void WriteTo(StringBuilder builder)
    {
        for (var i = 0; i < SlotCount; i++)
            GetSlot(i).WriteTo(builder);
    }

    /// <summary>The total width of <paramref name="nodes"/>, for a parent's own width.</summary>
    protected static int SumWidths<T>(ImmutableArray<T> nodes) where T : GreenNode
    {
        var width = 0;
        foreach (var node in nodes)
            width += node.FullWidth;
        return width;
    }
}
