using System.Collections.Immutable;

namespace Norristown.SyntaxGenerator;

/// <summary>
/// Represents one node of the table in Syntax.xml, which gives the class to generate, the class it
/// derives from, and its properties.
/// </summary>
/// <param name="Name">The class's name.</param>
/// <param name="Base">The class it derives from.</param>
/// <param name="Kinds">The kinds whose nodes are of this class, or empty for an abstract class.</param>
/// <param name="Summary">The class's summary, one string per line.</param>
/// <param name="IsAbstract">Whether the class is abstract, and so has no kind and no visitor method.</param>
/// <param name="IsInternal">Whether the class is internal, and so has no visitor method.</param>
/// <param name="IsHandWritten">Whether the class is written by hand, with only its <c>Accept</c> generated.</param>
/// <param name="IsPartial">Whether a hand-written partial class adds members the table cannot describe.</param>
/// <param name="IsMissingNode">Whether a node of this kind fills a place where the source contains nothing.</param>
/// <param name="Layout">
/// The slot order, by name, when it differs from the default of the base's slots followed by this
/// node's own, or empty otherwise.
/// </param>
/// <param name="Slots">The class's properties, in the order the table lists them.</param>
/// <param name="Line">The one-based line of the table that declares the node, where a problem with it is reported.</param>
public sealed record NodeRow(
    string Name,
    string Base,
    ImmutableArray<string> Kinds,
    ImmutableArray<string> Summary,
    bool IsAbstract,
    bool IsInternal,
    bool IsHandWritten,
    bool IsPartial,
    bool IsMissingNode,
    ImmutableArray<string> Layout,
    ImmutableArray<NodeSlot> Slots,
    int Line)
{
    /// <summary>
    /// Gets the name without the <c>Syntax</c> suffix, which is also the name of the kind and of the
    /// visitor method.
    /// </summary>
    public string BareName =>
        Name.EndsWith("Syntax", StringComparison.Ordinal)
            ? Name.Substring(0, Name.Length - "Syntax".Length)
            : Name;

    /// <summary>
    /// Gets a value indicating whether the class gets a <c>VisitXxx</c> method of its own on the
    /// visitors.
    /// </summary>
    public bool HasVisitMethod => !IsAbstract && !IsInternal;

    /// <summary>
    /// Gets the properties this row declares that are slots of the node rather than derived
    /// properties, in the order the table lists them.
    /// </summary>
    public IEnumerable<NodeSlot> Pieces => Slots.Where(slot => slot.IsPiece);
}
