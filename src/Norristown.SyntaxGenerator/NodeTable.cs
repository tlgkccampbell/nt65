using System.Collections.Immutable;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Norristown.SyntaxGenerator;

/// <summary>
/// The node table, read from <c>src/Norristown.Core/Syntax/Syntax.xml</c>: a <c>Tree</c> of
/// <c>Node</c> and <c>AbstractNode</c> elements, after Roslyn's own <c>Syntax.xml</c>. The table's
/// own header says what the elements and attributes mean.
/// </summary>
public static class NodeTable
{
    /// <summary>Where the table is, relative to the repository.</summary>
    public const string File = "src/Norristown.Core/Syntax/Syntax.xml";

    /// <summary>Reads <paramref name="text"/> as a table, in the order it writes its nodes.</summary>
    /// <param name="text">The table's text.</param>
    public static ImmutableArray<NodeRow> Read(string text)
    {
        var root = XDocument.Parse(text, LoadOptions.SetLineInfo).Root
            ?? throw new InvalidOperationException("the node table holds nothing");
        if (root.Name.LocalName != "Tree")
            throw Bad(root, $"the node table starts with <{root.Name.LocalName}>, not <Tree>");

        var nodes = ImmutableArray.CreateBuilder<NodeRow>();
        var named = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var element in root.Elements())
        {
            var isAbstract = element.Name.LocalName == "AbstractNode";
            if (!isAbstract && element.Name.LocalName != "Node")
                throw Bad(element, $"the node table does not understand <{element.Name.LocalName}>");
            var node = ReadNode(element, isAbstract);

            // One row per class: a second row of the same name would write the same file twice,
            // and the one the generator kept would be whichever the table listed last.
            if (named.TryGetValue(node.Name, out var first))
            {
                throw Bad(element,
                    $"{node.Name} is written twice, here and at line {((IXmlLineInfo)first).LineNumber}");
            }
            named.Add(node.Name, element);
            nodes.Add(node);
        }

        // The chain of classes above a node has to end: a chain of bases that loops back on
        // itself is a hierarchy with no top, and every walk up it would run forever.
        var rows = nodes.ToDictionary(node => node.Name, StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            var row = node;
            for (var step = 0; rows.TryGetValue(row.Base, out var above); step++)
            {
                row = above;
                if (row.Name == node.Name || step > rows.Count)
                    throw Bad(named[node.Name], $"the classes above {node.Name} run in a circle, through {node.Base}");
            }
        }
        return nodes.ToImmutable();
    }

    private static NodeRow ReadNode(XElement element, bool isAbstract)
    {
        Known(element, "Name", "Base", "Internal", "HandWritten", "Partial", "Missing", "Layout");
        var slots = ImmutableArray.CreateBuilder<NodeSlot>();
        foreach (var child in element.Elements())
        {
            var role = child.Name.LocalName switch
            {
                "Field" => SlotRole.Slot,
                "Member" => SlotRole.Member,
                "Kind" or "TypeComment" => (SlotRole?)null,
                _ => throw Bad(child, $"the node table does not understand <{child.Name.LocalName}>"),
            };
            if (role is { } held)
                slots.Add(ReadSlot(child, held));
        }

        return new NodeRow(
            Required(element, "Name"),
            Required(element, "Base"),
            Kinds(element),
            Summary(element, "TypeComment"),
            isAbstract,
            Flag(element, "Internal"),
            Flag(element, "HandWritten"),
            Flag(element, "Partial"),
            Flag(element, "Missing"),
            Words(element.Attribute("Layout")?.Value),
            slots.ToImmutable());
    }

    private static NodeSlot ReadSlot(XElement element, SlotRole role)
    {
        Known(element, "Name", "Type", "Optional");
        var read = "";
        foreach (var child in element.Elements())
        {
            switch (child.Name.LocalName)
            {
                case "Read":
                    read = child.Value;
                    break;
                case "Kind" or "PropertyComment":
                    break;
                default:
                    throw Bad(child, $"the node table does not understand <{child.Name.LocalName}>");
            }
        }

        // A <Field> reads its slot from the node's layout; a <Member> has no slot, and has to say
        // what it returns in a <Read>.
        if ((read.Length > 0) != (role == SlotRole.Member))
        {
            throw Bad(element, role == SlotRole.Member
                ? $"<Member> {Required(element, "Name")} wants a <Read>"
                : $"<Field> {Required(element, "Name")} reads its slot, and takes no <Read>");
        }

        return new NodeSlot(
            Required(element, "Name"),
            Required(element, "Type") + (Flag(element, "Optional") ? "?" : ""),
            Summary(element, "PropertyComment"),
            read,
            role,
            Kinds(element));
    }

    /// <summary>The kinds named by <paramref name="element"/>'s <c>Kind</c> children, which nodes and token fields both have.</summary>
    private static ImmutableArray<string> Kinds(XElement element)
    {
        var kinds = ImmutableArray.CreateBuilder<string>();
        foreach (var kind in element.Elements("Kind"))
        {
            Known(kind, "Name");
            kinds.Add(Required(kind, "Name"));
        }
        return kinds.ToImmutable();
    }

    /// <summary>
    /// The summary inside <paramref name="owner"/>'s <paramref name="wrapper"/> element, one
    /// trimmed string per non-blank line, kept as XML text: markup in the summary, such as
    /// <c>&lt;c&gt;</c> or <c>&lt;see&gt;</c>, comes back out as it went in, for the generated
    /// doc comment.
    /// </summary>
    private static ImmutableArray<string> Summary(XElement owner, string wrapper)
    {
        if (owner.Element(wrapper) is not { } comment)
            return ImmutableArray<string>.Empty;
        var summary = comment.Element("summary")
            ?? throw Bad(comment, $"<{wrapper}> holds a <summary> and nothing else");

        var text = new StringBuilder();
        foreach (var node in summary.Nodes())
            Write(text, node);
        var lines = ImmutableArray.CreateBuilder<string>();
        foreach (var line in text.ToString().Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (line.Trim().Length > 0)
                lines.Add(line.Trim());
        }
        return lines.ToImmutable();
    }

    /// <summary>Writes <paramref name="node"/> back out as the XML text it was read from.</summary>
    private static void Write(StringBuilder text, XNode node)
    {
        switch (node)
        {
            case XText content:
                text.Append(Escape(content.Value));
                break;
            case XElement element:
                text.Append('<').Append(element.Name.LocalName);
                foreach (var attribute in element.Attributes())
                    text.Append(' ').Append(attribute.Name.LocalName).Append("=\"").Append(Escape(attribute.Value)).Append('"');
                if (!element.Nodes().Any())
                {
                    text.Append("/>");
                    break;
                }
                text.Append('>');
                foreach (var child in element.Nodes())
                    Write(text, child);
                text.Append("</").Append(element.Name.LocalName).Append('>');
                break;
            default:
                break;
        }
    }

    private static string Escape(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static ImmutableArray<string> Words(string? value) =>
        value is null
            ? ImmutableArray<string>.Empty
            : ImmutableArray.CreateRange(value.Split([' '], StringSplitOptions.RemoveEmptyEntries));

    private static bool Flag(XElement element, string name) => element.Attribute(name)?.Value == "true";

    private static string Required(XElement element, string name) =>
        element.Attribute(name)?.Value ?? throw Bad(element, $"<{element.Name.LocalName}> wants a {name}");

    /// <summary>Checks that <paramref name="element"/> carries no attribute the table has no meaning for.</summary>
    private static void Known(XElement element, params string[] names)
    {
        foreach (var attribute in element.Attributes())
        {
            if (Array.IndexOf(names, attribute.Name.LocalName) < 0)
                throw Bad(element, $"<{element.Name.LocalName}> has no {attribute.Name.LocalName}");
        }
    }

    private static InvalidOperationException Bad(XElement element, string message) =>
        new($"{message}, at line {((IXmlLineInfo)element).LineNumber}");
}
