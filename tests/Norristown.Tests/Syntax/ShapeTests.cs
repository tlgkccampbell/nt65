using System.Collections.Immutable;
using Norristown.Syntax;
using Norristown.SyntaxGenerator;
using GreenNode = Norristown.Syntax.InternalSyntax.GreenNode;
using GreenSeparatedList = Norristown.Syntax.InternalSyntax.GreenSeparatedList;
using GreenSyntax = Norristown.Syntax.InternalSyntax.GreenSyntax;
using GreenToken = Norristown.Syntax.InternalSyntax.GreenToken;

namespace Norristown.Tests.Syntax;

/// <summary>
/// Every node the parser builds has the shape its row in the node table describes: it is its
/// kind's own green class, it has a slot for each piece in source order, a required slot holds
/// something, and what a slot holds is the type or one of the kinds the table names. It is asked
/// of every source in the repository and of every way of cutting its lines short, so a node the
/// parser only builds from a half-written line is held to it too.
/// <para>
/// <see cref="Unconverted"/> lists the kinds the parser still builds the generic node for. It is
/// read strictly both ways: a kind that is not on it must be right everywhere, and a kind on it
/// must still be wrong somewhere, so a line cannot outlive the work it stands for.
/// </para>
/// </summary>
public sealed class ShapeTests
{
    /// <summary>Where the kinds the parser has not yet fixed the slots of are listed, one to a line.</summary>
    private const string Unconverted = "tests/Norristown.Tests/Syntax/UnconvertedKinds.txt";

    [Fact]
    public void EveryNodeHasTheShapeItsRowDescribes()
    {
        var table = new NodeTree(NodeTable.Read(Repo.ReadText(Repo.Path(NodeTable.File.Split('/')))));
        var allowed = Allowed();
        var failures = Repo.CollectFailures(Repo.Sources(), path =>
        {
            var problems = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var variant in BrokenLines.Variants(Repo.ReadText(path)))
            {
                var tree = SyntaxTree.Parse(Repo.Named(path), variant);
                foreach (var node in tree.Root.DescendantNodes())
                    Check(table, node.Green, problems);
            }
            return problems.Where(problem => !allowed.Contains(problem.Key))
                .Select(problem => $"{Repo.Named(path)}: {problem.Value}");
        });
        Assert.True(failures.Count == 0, string.Join("\n", failures.Distinct().Take(40)));
    }

    /// <summary>
    /// Every kind the list allows is still one the parser builds the generic node for. A kind
    /// whose nodes all have their slots, or that nothing in the repository writes, has no place
    /// on it.
    /// </summary>
    [Fact]
    public void EveryKindAllowedIsStillUnconverted()
    {
        var table = new NodeTree(NodeTable.Read(Repo.ReadText(Repo.Path(NodeTable.File.Split('/')))));
        var found = Repo.CollectFailures(Repo.Sources(), path =>
        {
            var problems = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var variant in BrokenLines.Variants(Repo.ReadText(path)))
            {
                var tree = SyntaxTree.Parse(Repo.Named(path), variant);
                foreach (var node in tree.Root.DescendantNodes())
                    Check(table, node.Green, problems);
            }
            return problems.Keys;
        }).ToHashSet(StringComparer.Ordinal);

        var stale = Allowed().Where(kind => !found.Contains(kind)).ToList();
        Assert.True(
            stale.Count == 0,
            $"these kinds are converted and no longer belong in {Unconverted}:\n{string.Join("\n", stale)}");
    }

    private static HashSet<string> Allowed() =>
        [.. Repo.ReadText(Repo.Path(Unconverted.Split('/')))
            .ReplaceLineEndings("\n")
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))];

    /// <summary>What is wrong with <paramref name="green"/>, if anything, keyed by its kind.</summary>
    private static void Check(NodeTree table, GreenNode green, SortedDictionary<string, string> problems)
    {
        var kind = green.Kind.ToString();
        if (table.Nodes.FirstOrDefault(row => row.Kinds.Contains(kind)) is not { IsHandWritten: false } row)
            return;

        void Fail(string what) => problems.TryAdd(kind, what);

        if (green is GreenSyntax)
        {
            Fail($"{kind} is a GreenSyntax, not a {row.Name}");
            return;
        }
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
                if (slot.IsRequired)
                    Fail($"{row.Name}.{slot.Name} is required and its slot is empty");
                continue;
            }
            if (Wrong(table, slot, held) is { } wrong)
                Fail($"{row.Name}.{slot.Name} holds {wrong}");
        }
    }

    /// <summary>What <paramref name="held"/> is, when it is not what <paramref name="slot"/> takes.</summary>
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

    /// <summary>What <paramref name="held"/> is, when it is not a <paramref name="wanted"/>.</summary>
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
