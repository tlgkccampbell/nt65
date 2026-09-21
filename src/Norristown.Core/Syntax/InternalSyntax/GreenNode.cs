using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// An immutable node with a kind, a width and children, and no parent or position, so an
/// unchanged line keeps its node across edits wherever the edit moved it.
/// </summary>
/// <param name="kind">The node's kind.</param>
/// <param name="fullWidth">The node's width, trivia included.</param>
internal abstract class GreenNode(SyntaxKind kind, int fullWidth)
{
    // What each annotated node carries. It is a table beside the nodes rather than a field on
    // every one of them because a parse annotates nothing: the parser never writes here, and a
    // file none of whose nodes is tagged pays not one byte for the ones that could be. The
    // diagnostics went the other way for the opposite reason — the parser reports on them, and a
    // lookup on every node that holds one would be paid at every parse.
    private static readonly ConditionalWeakTable<GreenNode, SyntaxAnnotation[]> carried = new();

    private ImmutableArray<GreenDiagnostic> diagnostics;

    /// <summary>What the node is.</summary>
    public SyntaxKind Kind { get; } = kind;

    /// <summary>Width including trivia.</summary>
    public int FullWidth { get; } = fullWidth;

    /// <summary>What this node or something under it holds, rolled up as the node is built.</summary>
    public GreenFlags Flags { get; private protected set; }

    /// <summary>
    /// Whether this node or anything under it carries a diagnostic, so that collecting the
    /// diagnostics of a file walks only the subtrees that have any.
    /// </summary>
    public bool ContainsDiagnostics => (Flags & GreenFlags.ContainsDiagnostics) != 0;

    /// <summary>
    /// Whether this node or anything under it carries an annotation, so that looking for an
    /// annotated piece walks only the subtrees that hold one.
    /// </summary>
    public bool ContainsAnnotations => (Flags & GreenFlags.ContainsAnnotations) != 0;

    /// <summary>The diagnostics on this node itself, in the order they were reported.</summary>
    public ImmutableArray<GreenDiagnostic> Diagnostics =>
        diagnostics.IsDefault ? ImmutableArray<GreenDiagnostic>.Empty : diagnostics;

    /// <summary>The annotations on this node itself, in the order they were put on.</summary>
    public ImmutableArray<SyntaxAnnotation> Annotations =>
        ContainsAnnotations && carried.TryGetValue(this, out var own)
            ? ImmutableCollectionsMarshal.AsImmutableArray(own)
            : ImmutableArray<SyntaxAnnotation>.Empty;

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
        Flags |= GreenFlags.ContainsDiagnostics;
    }

    /// <summary>
    /// A copy of this node carrying <paramref name="wanted"/> in place of the annotations it has,
    /// or this node itself where it has none and none is wanted. The copy holds the same children,
    /// the same width and the same diagnostics — an annotation is no part of what a node says —
    /// and is a different object, which is the whole of what makes it findable. It is copied
    /// rather than rebuilt so that every kind of node, generated and hand-written, is covered by
    /// this one method.
    /// </summary>
    /// <param name="wanted">The annotations the copy is to carry.</param>
    internal GreenNode WithAnnotations(ImmutableArray<SyntaxAnnotation> wanted)
    {
        if (wanted.IsEmpty && Annotations.IsEmpty)
            return this;
        var copy = (GreenNode)MemberwiseClone();
        copy.Flags = !wanted.IsEmpty || AnyAnnotations()
            ? Flags | GreenFlags.ContainsAnnotations
            : Flags & ~GreenFlags.ContainsAnnotations;
        if (!wanted.IsEmpty)
            carried.Add(copy, ImmutableCollectionsMarshal.AsArray(wanted)!);
        return copy;
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

    /// <summary>
    /// Rolls <see cref="Flags"/> up from <paramref name="nodes"/>, which is what a parent holding
    /// a run of children does as it is built. The generated nodes, whose children are named slots
    /// rather than a run, write the same one assignment out.
    /// </summary>
    /// <param name="nodes">The children to roll up from.</param>
    private protected void RollUp<T>(ImmutableArray<T> nodes) where T : GreenNode
    {
        foreach (var node in nodes)
            Flags |= node.Flags;
    }

    /// <summary>Whether any child of this node holds an annotation, or holds one below it.</summary>
    private bool AnyAnnotations()
    {
        for (var i = 0; i < SlotCount; i++)
        {
            if (GetSlot(i) is { ContainsAnnotations: true })
                return true;
        }
        return false;
    }
}
