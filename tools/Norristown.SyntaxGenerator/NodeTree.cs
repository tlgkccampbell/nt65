using System.Collections.Immutable;

namespace Norristown.SyntaxGenerator;

/// <summary>
/// The table as a hierarchy: what derives from what, and the fixed slot layout of every
/// concrete node, which is the slots the classes above it write and then its own. A node whose
/// slots are not written in that order says the order itself, with <c>layout</c>.
/// </summary>
public sealed class NodeTree
{
    private readonly Dictionary<string, NodeRow> byName;
    private readonly HashSet<string> derivedFrom;
    private readonly Dictionary<string, ImmutableArray<LaidOutSlot>> layouts = [];

    /// <summary>Reads <paramref name="nodes"/> as a hierarchy, and checks it hangs together.</summary>
    /// <param name="nodes">The table.</param>
    public NodeTree(ImmutableArray<NodeRow> nodes)
    {
        Nodes = nodes;
        byName = nodes.ToDictionary(node => node.Name, StringComparer.Ordinal);
        derivedFrom = [.. nodes.Select(node => node.Base)];
        foreach (var node in nodes)
            CheckConversion(node);
    }

    /// <summary>The table, in the order it writes its nodes.</summary>
    public ImmutableArray<NodeRow> Nodes { get; }

    /// <summary>Whether another node derives from <paramref name="node"/>, which is what keeps it unsealed.</summary>
    /// <param name="node">The node.</param>
    public bool HasHeirs(NodeRow node) => derivedFrom.Contains(node.Name);

    /// <summary>
    /// Whether the node's properties read its slots outright: the parser builds the typed green
    /// node, or nothing builds the node at all and there is nothing else to read.
    /// </summary>
    /// <param name="node">The node.</param>
    public static bool ReadsSlots(NodeRow node) => node.IsConverted || node.IsUnbuilt;

    /// <summary>The node's slots in source order, each with the row that writes it.</summary>
    /// <param name="node">The node.</param>
    public ImmutableArray<LaidOutSlot> Layout(NodeRow node)
    {
        if (layouts.TryGetValue(node.Name, out var held))
            return held;

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

        var laid = pieces.Select((piece, index) => new LaidOutSlot(piece.Declarer, piece.Slot, index)).ToImmutableArray();
        layouts[node.Name] = laid;
        return laid;
    }

    /// <summary>The green class <paramref name="node"/>'s green class derives from.</summary>
    /// <param name="node">The node.</param>
    public string GreenBase(NodeRow node) =>
        byName.TryGetValue(node.Base, out var row) && !row.IsHandWritten ? node.Base : "GreenNode";

    /// <summary>
    /// The green type of <paramref name="slot"/>: what the typed constructor takes for it. A node
    /// slot takes the green class of its own type once every kind that class covers builds one,
    /// and a bare green node until then, so a kind can have its slots fixed without waiting for
    /// whatever it holds.
    /// </summary>
    /// <param name="slot">The slot.</param>
    public string GreenType(NodeSlot slot) => slot.List switch
    {
        ListShape.Separated => "GreenSeparatedList?",
        ListShape.Nodes or ListShape.Tokens => "GreenList?",
        _ when slot.IsToken => slot.IsRequired ? "GreenToken" : "GreenToken?",
        _ => (Typed(slot.BareType) ? slot.BareType : "GreenNode") + (slot.IsRequired ? "" : "?"),
    };

    /// <summary>Whether every node <paramref name="name"/> covers is built as its own green class.</summary>
    /// <param name="name">The name of a red class a slot may hold.</param>
    public bool Typed(string name) =>
        byName.TryGetValue(name, out var row) && !row.IsHandWritten
        && Nodes.Where(below => !below.IsAbstract && !below.IsHandWritten && Ancestry(below).Contains(row))
            .All(ReadsSlots);

    /// <summary>
    /// Whether <paramref name="slot"/> has the name of a property a class above
    /// <paramref name="node"/> still carries, and so hides it.
    /// </summary>
    /// <param name="node">The node.</param>
    /// <param name="slot">The slot the node declares.</param>
    public bool Hides(NodeRow node, NodeSlot slot) =>
        Ancestry(node).Any(row => row != node && row.Slots.Any(other => other.Name == slot.Name));

    /// <summary>The classes above <paramref name="node"/>, outermost first, and then it.</summary>
    /// <param name="node">The node.</param>
    public IEnumerable<NodeRow> Ancestry(NodeRow node)
    {
        var chain = new List<NodeRow>();
        for (var row = node; row is not null; row = byName.GetValueOrDefault(row.Base))
            chain.Insert(0, row);
        return chain;
    }

    /// <summary>
    /// A kind converts with the family that writes its slots: a property declared above it has
    /// one type, so the class that declares it and every class that overrides it agree.
    /// </summary>
    private void CheckConversion(NodeRow node)
    {
        if (ReadsSlots(node))
        {
            foreach (var above in Ancestry(node).Where(row => row != node && row.Pieces.Any() && !ReadsSlots(row)))
            {
                throw new InvalidOperationException(
                    $"{node.Name} reads its slots but {above.Name}, which writes some of them, does not");
            }
        }

        if (!node.IsAbstract || !node.Pieces.Any() || !ReadsSlots(node))
            return;
        foreach (var below in Nodes.Where(row =>
            !row.IsAbstract && row != node && Ancestry(row).Contains(node) && !ReadsSlots(row)))
        {
            throw new InvalidOperationException(
                $"{node.Name} reads its slots, so {below.Name}, which derives from it, must too");
        }
    }
}
