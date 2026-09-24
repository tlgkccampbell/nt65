using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// Represents an immutable node with a kind, a width and children, and no parent or position, so
/// an unchanged line keeps its node across edits, even when an edit moves it.
/// </summary>
/// <param name="kind">The node's kind.</param>
/// <param name="fullWidth">The node's width, trivia included.</param>
internal abstract class GreenNode(SyntaxKind kind, int fullWidth)
{
    // The annotations each annotated node has. They live in a side table rather than in a field
    // on every node because parsing never annotates anything. The parser never adds entries here,
    // so a file with no annotated nodes pays no memory for them. Diagnostics use a field for the
    // opposite reason: the parser does report them, and a table lookup for every node that has
    // one would be paid on every parse.
    private static readonly ConditionalWeakTable<GreenNode, SyntaxAnnotation[]> annotationTable = new();

    // The nodes that more than one place in a file may hold, which Report checks against. Only a
    // debug build adds to it, so a release build pays nothing beyond the empty table.
    private static readonly ConditionalWeakTable<GreenNode, GreenNode> sharedNodes = new();

    private ImmutableArray<GreenDiagnostic> diagnostics;

    /// <summary>Gets the node's kind.</summary>
    public SyntaxKind Kind { get; } = kind;

    /// <summary>Gets the node's width, including trivia.</summary>
    public int FullWidth { get; } = fullWidth;

    /// <summary>Gets what this node or something under it holds, rolled up as the node is built.</summary>
    public GreenFlags Flags { get; private protected set; }

    /// <summary>
    /// Gets a value indicating whether this node or anything under it has a diagnostic, so that
    /// collecting the diagnostics of a file walks only the subtrees that have any.
    /// </summary>
    public bool ContainsDiagnostics => (Flags & GreenFlags.ContainsDiagnostics) != 0;

    /// <summary>
    /// Gets a value indicating whether this node or anything under it has an annotation, so that
    /// looking for an annotated node or token walks only the subtrees that hold one.
    /// </summary>
    public bool ContainsAnnotations => (Flags & GreenFlags.ContainsAnnotations) != 0;

    /// <summary>Gets the diagnostics on this node itself, in the order they were reported.</summary>
    public ImmutableArray<GreenDiagnostic> Diagnostics =>
        diagnostics.IsDefault ? ImmutableArray<GreenDiagnostic>.Empty : diagnostics;

    /// <summary>Gets the annotations on this node itself, in the order they were added.</summary>
    public ImmutableArray<SyntaxAnnotation> Annotations =>
        ContainsAnnotations && annotationTable.TryGetValue(this, out var own)
            ? ImmutableCollectionsMarshal.AsImmutableArray(own)
            : ImmutableArray<SyntaxAnnotation>.Empty;

    /// <summary>Gets the number of child slots the node has.</summary>
    public abstract int SlotCount { get; }

    /// <summary>
    /// Gets a value indicating whether the node fills a place the grammar requires but the source
    /// does not contain, such as a missing token, or the empty expression the parser leaves where
    /// it could not read one.
    /// </summary>
    public virtual bool IsMissing => false;

    /// <summary>
    /// Returns the child at <paramref name="index"/>, from 0 to <see cref="SlotCount"/> − 1, or
    /// null for a slot the source leaves out, such as an absent optional piece or a list with no
    /// items.
    /// </summary>
    public abstract GreenNode? GetSlot(int index);

    /// <summary>Returns the node's text, exactly as in the source.</summary>
    public string ToFullString()
    {
        var builder = new StringBuilder(FullWidth);
        WriteTo(builder);
        return builder.ToString();
    }

    /// <summary>
    /// Adds <paramref name="diagnostic"/> to this node. A node is given its diagnostics while the
    /// parser still has it in hand, before it goes into a parent, so that every parent can roll
    /// <see cref="ContainsDiagnostics"/> up in its constructor. A node object shared by more than
    /// one place in the file, such as a token from the lexer's cache or the shared missing token
    /// of a kind, is never given one, and keeps only what its constructor gave it. Such a node is
    /// marked with <see cref="MarkShared"/>, and a debug build asserts that it is not reported on.
    /// </summary>
    /// <param name="diagnostic">The diagnostic, placed within this node.</param>
    internal void Report(GreenDiagnostic diagnostic)
    {
        Debug.Assert(!sharedNodes.TryGetValue(this, out _), "a shared node is never given a diagnostic");
        diagnostics = Diagnostics.Add(diagnostic);
        Flags |= GreenFlags.ContainsDiagnostics;
    }

    /// <summary>
    /// Records that this node may be held by more than one place in a file, so that a debug build
    /// fails when <see cref="Report"/> is given it. A release build removes every call.
    /// </summary>
    [Conditional("DEBUG")]
    internal void MarkShared() => sharedNodes.AddOrUpdate(this, this);

    /// <summary>
    /// Returns a copy of this node with <paramref name="wanted"/> in place of the annotations it
    /// has, or this node itself if it has none and none are wanted. The copy holds the same
    /// children, the same width and the same diagnostics, because an annotation is not part of the
    /// node's content. The copy is a different object, and that identity alone makes it findable.
    /// The node is copied rather than rebuilt so that this one method covers every kind of node,
    /// generated and hand-written.
    /// </summary>
    /// <param name="wanted">The annotations the copy is to have.</param>
    internal GreenNode WithAnnotations(ImmutableArray<SyntaxAnnotation> wanted)
    {
        if (wanted.IsEmpty && Annotations.IsEmpty)
            return this;
        var copy = (GreenNode)MemberwiseClone();
        copy.Flags = !wanted.IsEmpty || AnyAnnotations()
            ? Flags | GreenFlags.ContainsAnnotations
            : Flags & ~GreenFlags.ContainsAnnotations;
        if (!wanted.IsEmpty)
            annotationTable.Add(copy, ImmutableCollectionsMarshal.AsArray(wanted)!);
        return copy;
    }

    /// <summary>
    /// Returns a copy of this node that has <paramref name="annotations"/> after the annotations
    /// it already has, or this node itself if every one of them is already there. An annotation
    /// the node has, or one listed twice, is added only once.
    /// </summary>
    /// <param name="annotations">The annotations to add.</param>
    internal GreenNode WithAdditionalAnnotations(IEnumerable<SyntaxAnnotation> annotations)
    {
        var own = Annotations;
        var wanted = own.AddRange(annotations.Where(annotation => !own.Contains(annotation)).Distinct());
        return wanted.Length == own.Length ? this : WithAnnotations(wanted);
    }

    /// <summary>
    /// Returns a copy of this node without <paramref name="annotations"/>, keeping its other
    /// annotations, or this node itself if it has none of them.
    /// </summary>
    /// <param name="annotations">The annotations to remove.</param>
    internal GreenNode WithoutAnnotations(IEnumerable<SyntaxAnnotation> annotations)
    {
        var own = Annotations;
        var kept = own.RemoveRange(annotations);
        return kept.Length == own.Length ? this : WithAnnotations(kept);
    }

    /// <summary>
    /// Returns a copy of this node without its annotations of kind <paramref name="kind"/>, or
    /// this node itself if it has none of that kind.
    /// </summary>
    /// <param name="kind">The kind of annotation to remove.</param>
    internal GreenNode WithoutAnnotations(string kind)
    {
        var own = Annotations;
        var kept = own.RemoveAll(annotation => annotation.Kind == kind);
        return kept.Length == own.Length ? this : WithAnnotations(kept);
    }

    /// <summary>
    /// Returns the annotations of kind <paramref name="kind"/> on this node itself, in the order
    /// they were added.
    /// </summary>
    /// <param name="kind">The kind to look for.</param>
    internal IEnumerable<SyntaxAnnotation> GetAnnotations(string kind) =>
        Annotations.Where(annotation => annotation.Kind == kind);

    /// <summary>
    /// Creates the red node for this node at <paramref name="position"/> under
    /// <paramref name="parent"/>.
    /// </summary>
    internal abstract SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position);

    internal virtual void WriteTo(StringBuilder builder)
    {
        for (var i = 0; i < SlotCount; i++)
            GetSlot(i)?.WriteTo(builder);
    }

    /// <summary>Returns the total width of <paramref name="nodes"/>, for a parent's own width.</summary>
    protected static int SumWidths<T>(ImmutableArray<T> nodes) where T : GreenNode
    {
        var width = 0;
        foreach (var node in nodes)
            width += node.FullWidth;
        return width;
    }

    /// <summary>
    /// Rolls <see cref="Flags"/> up from <paramref name="nodes"/>. A parent whose children are an
    /// array calls this as it is built. The generated nodes, whose children are named slots rather
    /// than an array, contain the same assignment for each slot instead.
    /// </summary>
    /// <param name="nodes">The children to roll up from.</param>
    private protected void RollUp<T>(ImmutableArray<T> nodes) where T : GreenNode
    {
        foreach (var node in nodes)
            Flags |= node.Flags;
    }

    /// <summary>
    /// Returns a value indicating whether any child of this node holds an annotation, either
    /// itself or below it.
    /// </summary>
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
