using System.Collections.Immutable;

namespace Norristown.SyntaxGenerator;

/// <summary>
/// The node table, read from <c>src/Norristown.Core/Syntax/Syntax.nodes</c>. The format is a
/// block per node, a line per key, and nesting by indentation; the table's own header says what
/// the keys mean.
/// </summary>
public static class NodeTable
{
    /// <summary>Where the table is, relative to the repository.</summary>
    public const string File = "src/Norristown.Core/Syntax/Syntax.nodes";

    /// <summary>Reads <paramref name="text"/> as a table, in the order it writes its nodes.</summary>
    /// <param name="text">The table's text.</param>
    public static ImmutableArray<NodeRow> Read(string text)
    {
        var nodes = ImmutableArray.CreateBuilder<NodeRow>();
        Builder? node = null;
        SlotBuilder? slot = null;

        void FinishSlot()
        {
            if (node is not null && slot is not null)
                node.Slots.Add(slot.Build());
            slot = null;
        }

        void FinishNode()
        {
            FinishSlot();
            if (node is not null)
                nodes.Add(node.Build());
            node = null;
        }

        foreach (var raw in text.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0 || line.TrimStart().StartsWith('#'))
                continue;

            var indent = line.Length - line.TrimStart().Length;
            var (key, value) = Split(line.TrimStart());
            switch (indent)
            {
                case 0 when key == "node":
                    FinishNode();
                    var (name, derivesFrom) = SplitOn(value, " : ");
                    node = new Builder(name, derivesFrom);
                    break;
                case 2 when key is "slot" or "member":
                    FinishSlot();
                    var (slotName, slotType) = SplitOn(value, " : ");
                    slot = new SlotBuilder(slotName, slotType, key == "slot");
                    break;
                case 2:
                    Apply(Require(node, line), key, value);
                    break;
                case 4:
                    Apply(Require(slot, line), key, value);
                    break;
                default:
                    throw new InvalidOperationException($"the node table does not understand `{line}`");
            }
        }

        FinishNode();
        return nodes.ToImmutable();
    }

    private static void Apply(Builder node, string key, string value)
    {
        switch (key)
        {
            case "kind":
                node.Kinds.AddRange(value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
                break;
            case "summary":
                node.Summary.Add(value);
                break;
            case "abstract":
                node.IsAbstract = true;
                break;
            case "internal":
                node.IsInternal = true;
                break;
            case "handwritten":
                node.IsHandWritten = true;
                break;
            case "partial":
                node.IsPartial = true;
                break;
            default:
                throw new InvalidOperationException($"the node table does not understand the key `{key}`");
        }
    }

    private static void Apply(SlotBuilder slot, string key, string value)
    {
        switch (key)
        {
            case "summary":
                slot.Summary.Add(value);
                break;
            case "read" or "cache":
                slot.Form = key;
                slot.Read = value;
                break;
            case "nodes":
                slot.Form = key;
                break;
            default:
                throw new InvalidOperationException($"the node table does not understand the key `{key}`");
        }
    }

    private static T Require<T>(T? held, string line) where T : class =>
        held ?? throw new InvalidOperationException($"the node table has `{line.Trim()}` outside anything it belongs to");

    private static (string Key, string Value) Split(string line)
    {
        var space = line.IndexOf(' ');
        return space < 0 ? (line, "") : (line[..space], line[(space + 1)..]);
    }

    private static (string Left, string Right) SplitOn(string text, string separator)
    {
        var at = text.IndexOf(separator, StringComparison.Ordinal);
        return at < 0
            ? throw new InvalidOperationException($"the node table wants `{separator}` in `{text}`")
            : (text[..at], text[(at + separator.Length)..]);
    }

    private sealed class Builder(string name, string derivesFrom)
    {
        public List<string> Kinds { get; } = [];

        public List<string> Summary { get; } = [];

        public List<NodeSlot> Slots { get; } = [];

        public bool IsAbstract { get; set; }

        public bool IsInternal { get; set; }

        public bool IsHandWritten { get; set; }

        public bool IsPartial { get; set; }

        public NodeRow Build() => new(
            name, derivesFrom, [.. Kinds], [.. Summary], IsAbstract, IsInternal, IsHandWritten, IsPartial, [.. Slots]);
    }

    private sealed class SlotBuilder(string name, string type, bool isPiece)
    {
        public List<string> Summary { get; } = [];

        public string Form { get; set; } = "read";

        public string Read { get; set; } = "";

        public NodeSlot Build() => new(name, type, [.. Summary], Form, Read, isPiece);
    }
}
