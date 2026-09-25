using System.Collections.Immutable;
using Norristown.Syntax;
using Norristown.SyntaxGenerator;
using GreenNode = Norristown.Syntax.InternalSyntax.GreenNode;
using GreenSeparatedList = Norristown.Syntax.InternalSyntax.GreenSeparatedList;
using GreenToken = Norristown.Syntax.InternalSyntax.GreenToken;

namespace Norristown.Tests.Syntax;

/// <summary>
/// Checks that every node the parser builds has the shape its row in the node table describes.
/// The node is its kind's own green class and has a slot for each child element in source order.
/// A required slot holds something, and what a slot holds is the type or one of the kinds the
/// table names. The check runs over every source in the repository and every way of cutting its
/// lines short, so a node the parser builds only from a half-written line is checked too.
/// </summary>
public sealed class ShapeTests
{
    [Fact]
    public void EveryNodeHasTheShapeItsRowDescribes()
    {
        var table = new NodeTree(NodeTable.Read(Repo.ReadText(Repo.Path(NodeTable.File.Split('/')))));
        var failures = Repo.CollectFailures(Repo.Sources(), path =>
        {
            var problems = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var variant in BrokenLines.Of(path))
            {
                var tree = SyntaxTree.Parse(Repo.Named(path), variant);
                foreach (var node in tree.Root.DescendantNodes())
                    Check(table, node.Green, problems);
            }
            return problems.Values.Select(problem => $"{Repo.Named(path)}: {problem}");
        });
        Assert.True(failures.Count == 0, string.Join("\n", failures.Distinct().Take(40)));
    }

    /// <summary>
    /// Records in <paramref name="problems"/> what is wrong with <paramref name="green"/>, if
    /// anything, keyed by its kind. Only the first problem found for a kind is kept.
    /// </summary>
    private static void Check(NodeTree table, GreenNode green, SortedDictionary<string, string> problems)
    {
        var kind = green.Kind.ToString();
        if (table.Nodes.FirstOrDefault(row => row.Kinds.Contains(kind)) is not { IsHandWritten: false } row)
            return;

        void Fail(string what) => problems.TryAdd(kind, what);

        if (green.GetType().Name != row.Name)
        {
            Fail($"{kind} is a {green.GetType().Name}, not a {row.Name}");
            return;
        }

        var layout = table.Layout(row);
        if (green.SlotCount != layout.Length)
        {
            Fail($"{row.Name} has {green.SlotCount} slots and the table gives it {layout.Length}");
            return;
        }
        foreach (var (_, slot, index) in layout)
        {
            if (green.GetSlot(index) is not { } held)
            {
                // A list is never absent: a slot with nothing in it is the list with no items.
                if (slot.IsRequired && slot.List == ListShape.None)
                    Fail($"{row.Name}.{slot.Name} is required and its slot is empty");
                continue;
            }
            if (Wrong(table, slot, held) is { } wrong)
                Fail($"{row.Name}.{slot.Name} holds {wrong}");
        }
    }

    /// <summary>
    /// Returns a description of <paramref name="held"/> when it is not what
    /// <paramref name="slot"/> takes, or null when it is.
    /// </summary>
    private static string? Wrong(NodeTree table, NodeSlot slot, GreenNode held)
    {
        switch (slot.List)
        {
            case ListShape.Separated when held is not GreenSeparatedList:
            case ListShape.Nodes or ListShape.Tokens when held.Kind != SyntaxKind.List:
                return $"{held.Kind}, and the slot is a {slot.Type}";
            case ListShape.Tokens:
                return Items(held).Any(item => item is not GreenToken) ? "a node among its tokens" : null;
            case ListShape.Nodes or ListShape.Separated:
                return Items(held).Where(item => item is not GreenToken)
                    .Select(item => Wrong(table, item, slot.ListItemType!))
                    .FirstOrDefault(problem => problem is not null);
            default:
                break;
        }

        if (slot.IsToken)
        {
            return held is GreenToken token
                ? slot.Kinds.Contains(token.Kind.ToString(), StringComparer.Ordinal) ? null
                    : $"a {token.Kind}, and the slot takes {string.Join(" or ", slot.Kinds)}"
                : $"a {held.Kind}, and the slot takes a token";
        }
        return Wrong(table, held, slot.BareType);
    }

    /// <summary>
    /// Returns a description of <paramref name="held"/> when it is not a
    /// <paramref name="wanted"/>, or null when it is.
    /// </summary>
    private static string? Wrong(NodeTree table, GreenNode held, string wanted)
    {
        if (wanted == "SyntaxNode")
            return null;
        var row = table.Nodes.FirstOrDefault(candidate => candidate.Kinds.Contains(held.Kind.ToString()));
        return row is not null && table.Ancestry(row).Any(above => above.Name == wanted)
            ? null
            : $"a {held.Kind}, and the slot takes a {wanted}";
    }

    private static ImmutableArray<GreenNode> Items(GreenNode list)
    {
        var items = ImmutableArray.CreateBuilder<GreenNode>(list.SlotCount);
        for (var i = 0; i < list.SlotCount; i++)
        {
            if (list.GetSlot(i) is { } item)
                items.Add(item);
        }
        return items.ToImmutable();
    }
}
