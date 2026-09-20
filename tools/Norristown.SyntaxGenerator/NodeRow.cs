using System.Collections.Immutable;

namespace Norristown.SyntaxGenerator;

/// <summary>One node of the table: the class to write, what it derives from, and its properties.</summary>
/// <param name="Name">The class's name.</param>
/// <param name="Base">The class it derives from.</param>
/// <param name="Kinds">The kinds whose nodes are of this class; empty for an abstract one.</param>
/// <param name="Summary">The class's summary, a line per line of it.</param>
/// <param name="IsAbstract">Whether the class is abstract, and so has no kind and no visitor method.</param>
/// <param name="IsInternal">Whether the class is internal, and so has no visitor method.</param>
/// <param name="IsHandWritten">Whether the class is written by hand and only its <c>Accept</c> generated.</param>
/// <param name="IsPartial">Whether a hand-written half holds members the table cannot say.</param>
/// <param name="IsConverted">Whether the parser builds this kind's typed green node.</param>
/// <param name="IsUnbuilt">Whether nothing builds this kind yet: it is a design waiting for its parser.</param>
/// <param name="IsMissingNode">Whether a node of this kind stands where one belongs that the source lacks.</param>
/// <param name="Layout">The slot order, by name, when it is not the base's slots and then this node's own.</param>
/// <param name="Slots">The class's properties, in the order they are written.</param>
public sealed record NodeRow(
    string Name,
    string Base,
    ImmutableArray<string> Kinds,
    ImmutableArray<string> Summary,
    bool IsAbstract,
    bool IsInternal,
    bool IsHandWritten,
    bool IsPartial,
    bool IsConverted,
    bool IsUnbuilt,
    bool IsMissingNode,
    ImmutableArray<string> Layout,
    ImmutableArray<NodeSlot> Slots)
{
    /// <summary>The name without the <c>Syntax</c> suffix, which is also the kind's and the visitor method's.</summary>
    public string BareName =>
        Name.EndsWith("Syntax", StringComparison.Ordinal) ? Name[..^"Syntax".Length] : Name;

    /// <summary>Whether the class gets a <c>VisitXxx</c> of its own on the visitors.</summary>
    public bool HasVisitMethod => !IsAbstract && !IsInternal;

    /// <summary>The pieces of the node this row declares, in the order it writes them.</summary>
    public IEnumerable<NodeSlot> Pieces => Slots.Where(slot => slot.IsPiece);
}
