using System.Collections.Immutable;

namespace Norristown.SyntaxGenerator;

/// <summary>
/// Represents the table as a hierarchy, recording which class derives from which, and the fixed
/// slot layout of every concrete node. By default a node's layout is the slots declared by the
/// classes above it, followed by its own. A node that needs another order gives it explicitly,
/// with a <c>Layout</c> attribute.
/// </summary>
public sealed class NodeTree
{
    /// <summary>
    /// The hand-written class that every node of the table derives from, directly or indirectly.
    /// </summary>
    private const string Root = "SyntaxNode";

    private readonly Dictionary<string, NodeRow> byName;
    private readonly HashSet<string> derivedFrom;
    private readonly Dictionary<string, ImmutableArray<LaidOutSlot>> layouts = [];

    /// <summary>Reads <paramref name="nodes"/> as a hierarchy, and checks that it is consistent.</summary>
    /// <param name="nodes">The table.</param>
    public NodeTree(ImmutableArray<NodeRow> nodes)
    {
        Nodes = nodes;
        byName = nodes.ToDictionary(node => node.Name, StringComparer.Ordinal);
        derivedFrom = [.. nodes.Select(node => node.Base)];

        // A Base must be another row or the root class that all red nodes derive from. Anything else
        // would surface as a C# error in a generated file nobody wrote, and a row that is its own
        // base would make the walk up the hierarchy loop forever.
        foreach (var node in nodes)
        {
            if (node.Base != Root && (node.Base == node.Name || !byName.ContainsKey(node.Base)))
            {
                throw new InvalidOperationException(
                    $"{node.Name} derives from {node.Base}, which is neither another node of the table nor {Root}");
            }
        }

        // Every layout is computed here, up front, so reading one later is a lookup and
        // concurrent readers are safe.
        foreach (var node in nodes)
            layouts[node.Name] = Laid(node);
    }

    /// <summary>Gets the table, with the nodes in the order it lists them.</summary>
    public ImmutableArray<NodeRow> Nodes { get; }

    /// <summary>
    /// Returns a value indicating whether another node derives from <paramref name="node"/>, which
    /// keeps <paramref name="node"/> unsealed.
    /// </summary>
    /// <param name="node">The node.</param>
    public bool HasHeirs(NodeRow node) => derivedFrom.Contains(node.Name);

    /// <summary>Returns the node's slots in source order, each with the row that declares it.</summary>
    /// <param name="node">The node.</param>
    public ImmutableArray<LaidOutSlot> Layout(NodeRow node) => layouts[node.Name];

    /// <summary>
    /// Returns the base class of <paramref name="node"/>'s green class, which is its base row's
    /// green class, or <c>GreenNode</c> when the base is the root or a hand-written class.
    /// </summary>
    /// <param name="node">The node.</param>
    public string GreenBase(NodeRow node) =>
        byName.TryGetValue(node.Base, out var row) && !row.IsHandWritten ? node.Base : "GreenNode";

    /// <summary>
    /// Returns the green type of <paramref name="slot"/>, which is the type the typed constructor
    /// takes for it. A node slot takes the green class of its own type, or a bare green node when
    /// the table gives its type no green class of its own, as for <c>SyntaxNode</c> or a class
    /// written by hand.
    /// </summary>
    /// <param name="slot">The slot.</param>
    public string GreenType(NodeSlot slot) => slot.List switch
    {
        ListShape.Separated => "GreenSeparatedList?",
        ListShape.Nodes or ListShape.Tokens => "GreenList?",
        _ when slot.IsToken => slot.IsRequired ? "GreenToken" : "GreenToken?",
        _ => (Typed(slot.BareType) ? slot.BareType : "GreenNode") + (slot.IsRequired ? "" : "?"),
    };

    /// <summary>
    /// Returns a value indicating whether <paramref name="name"/> is a class for which the table
    /// generates a green class.
    /// </summary>
    /// <param name="name">The name of a red class a slot may hold.</param>
    public bool Typed(string name) => byName.TryGetValue(name, out var row) && !row.IsHandWritten;

    /// <summary>
    /// Returns a value indicating whether <paramref name="slot"/> has the name of a property that
    /// a class above <paramref name="node"/> also declares, and so hides it.
    /// </summary>
    /// <param name="node">The node.</param>
    /// <param name="slot">The slot the node declares.</param>
    public bool Hides(NodeRow node, NodeSlot slot) =>
        Ancestry(node).Any(row => row != node && row.Slots.Any(other => other.Name == slot.Name));

    /// <summary>
    /// Returns the classes above <paramref name="node"/>, outermost first, followed by
    /// <paramref name="node"/> itself.
    /// </summary>
    /// <param name="node">The node.</param>
    public IEnumerable<NodeRow> Ancestry(NodeRow node)
    {
        var chain = new List<NodeRow>();
        for (var row = node; row is not null; row = byName.TryGetValue(row.Base, out var above) ? above : null)
            chain.Insert(0, row);
        return chain;
    }

    /// <summary>Computes the node's slots in source order.</summary>
    private ImmutableArray<LaidOutSlot> Laid(NodeRow node)
    {
        var pieces = new List<(NodeRow Declarer, NodeSlot Slot)>();
        foreach (var row in Ancestry(node))
            pieces.AddRange(row.Pieces.Select(piece => (row, piece)));

        if (!node.Layout.IsEmpty)
        {
            if (!node.Layout.OrderBy(name => name, StringComparer.Ordinal).SequenceEqual(
                    pieces.Select(piece => piece.Slot.Name).OrderBy(name => name, StringComparer.Ordinal),
                    StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    $"{node.Name}'s layout must name each of its slots exactly once");
            }
            pieces = [.. node.Layout.Select(name => pieces.First(piece => piece.Slot.Name == name))];
        }

        return [.. pieces.Select((piece, index) => new LaidOutSlot(piece.Declarer, piece.Slot, index))];
    }
}
