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
        foreach (var element in root.Elements())
        {
            var isAbstract = element.Name.LocalName == "AbstractNode";
            if (!isAbstract && element.Name.LocalName != "Node")
                throw Bad(element, $"the node table does not understand <{element.Name.LocalName}>");
            nodes.Add(ReadNode(element, isAbstract));
        }
        return nodes.ToImmutable();
    }

    private static NodeRow ReadNode(XElement element, bool isAbstract)
    {
        Known(element, "Name", "Base", "Internal", "HandWritten", "Partial", "Converted", "Unbuilt", "Missing", "Layout");
        var slots = ImmutableArray.CreateBuilder<NodeSlot>();
        foreach (var child in element.Elements())
        {
            var role = child.Name.LocalName switch
            {
                "Field" => SlotRole.Slot,
                "Member" => SlotRole.Member,
                "Legacy" => SlotRole.Legacy,
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
            Flag(element, "Converted"),
            Flag(element, "Unbuilt"),
            Flag(element, "Missing"),
            Words(element.Attribute("Layout")?.Value),
            slots.ToImmutable());
    }

    private static NodeSlot ReadSlot(XElement element, SlotRole role)
    {
        Known(element, "Name", "Type", "Optional", "Today");
        var form = "none";
        var read = "";
        foreach (var child in element.Elements())
        {
            switch (child.Name.LocalName)
            {
                case "Read":
                    form = "read";
                    read = child.Value;
                    break;
                case "Cache":
                    form = "cache";
                    read = child.Value;
                    break;
                case "Nodes":
                    form = "nodes";
                    break;
                case "Kind" or "PropertyComment":
                    break;
                default:
                    throw Bad(child, $"the node table does not understand <{child.Name.LocalName}>");
            }
        }

        return new NodeSlot(
            Required(element, "Name"),
            Required(element, "Type") + (Flag(element, "Optional") ? "?" : ""),
            Summary(element, "PropertyComment"),
            form,
            read,
            role,
            Kinds(element),
            element.Attribute("Today")?.Value);
    }

    /// <summary>The kinds <paramref name="element"/> names, which a node and a token field both do.</summary>
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
    /// The summary inside <paramref name="wrapper"/>, a line per line of it, as the XML it is
    /// written into: the markup a summary carries is the file's own, and comes back as it went in.
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
