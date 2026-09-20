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
    private ImmutableArray<GreenDiagnostic> diagnostics;

    /// <summary>What the node is.</summary>
    public SyntaxKind Kind { get; } = kind;

    /// <summary>Width including trivia.</summary>
    public int FullWidth { get; } = fullWidth;

    /// <summary>
    /// Whether this node or anything under it carries a diagnostic, so that collecting the
    /// diagnostics of a file walks only the subtrees that have any. Every node rolls it up from
    /// its children as it is built.
    /// </summary>
    public bool ContainsDiagnostics { get; private protected set; }

    /// <summary>The diagnostics on this node itself, in the order they were reported.</summary>
    public ImmutableArray<GreenDiagnostic> Diagnostics =>
        diagnostics.IsDefault ? ImmutableArray<GreenDiagnostic>.Empty : diagnostics;

    /// <summary>How many children the node has.</summary>
    public abstract int SlotCount { get; }

    /// <summary>
    /// Whether the node stands where one belongs that the source does not have: a missing token,
    /// or the empty expression the parser leaves where it could read none.
    /// </summary>
    public virtual bool IsMissing => false;

    /// <summary>
    /// The child at <paramref name="index"/>, from 0 to <see cref="SlotCount"/> − 1, or null for
    /// a slot the source leaves out: an optional piece not written, or a list with no items.
    /// </summary>
    public abstract GreenNode? GetSlot(int index);

    /// <summary>The node's text, exactly as in the source.</summary>
    public string ToFullString()
    {
        var builder = new StringBuilder(FullWidth);
        WriteTo(builder);
        return builder.ToString();
    }

    /// <summary>
    /// Gives this node <paramref name="diagnostic"/>. A node is given its diagnostics while the
    /// parser still has it in hand, before it goes into a parent, which is what lets every parent
    /// roll <see cref="ContainsDiagnostics"/> up in its constructor. A node that stands for more
    /// than one place in the file — a token the lexer shares, the missing token of a kind — is
    /// never given one: it carries what it has from its constructor.
    /// </summary>
    /// <param name="diagnostic">The diagnostic, placed within this node.</param>
    internal void Report(GreenDiagnostic diagnostic)
    {
        diagnostics = Diagnostics.Add(diagnostic);
        ContainsDiagnostics = true;
    }

    /// <summary>The red node for this one at <paramref name="position"/> under <paramref name="parent"/>.</summary>
    internal abstract SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position);

    internal virtual void WriteTo(StringBuilder builder)
    {
        for (var i = 0; i < SlotCount; i++)
            GetSlot(i)?.WriteTo(builder);
    }

    /// <summary>The total width of <paramref name="nodes"/>, for a parent's own width.</summary>
    protected static int SumWidths<T>(ImmutableArray<T> nodes) where T : GreenNode
    {
        var width = 0;
        foreach (var node in nodes)
            width += node.FullWidth;
        return width;
    }

    /// <summary>Whether any of <paramref name="nodes"/> holds a diagnostic, for a parent's own flag.</summary>
    protected static bool AnyDiagnostics<T>(ImmutableArray<T> nodes) where T : GreenNode
    {
        foreach (var node in nodes)
        {
            if (node.ContainsDiagnostics)
                return true;
        }
        return false;
    }
}
