using System.Collections.Immutable;
using System.Text;

namespace Norristown.SyntaxGenerator;

/// <summary>
/// Writes the C# the node table describes: a red class and a green class per node, the switch
/// from kind to class, and the visitors. It builds strings, which is enough for a table this
/// size, and what it makes is checked in, so the tests can ask for it in memory and compare.
/// </summary>
public static class SyntaxWriter
{
    /// <summary>The folders every generated file goes in, relative to the repository.</summary>
    public static readonly ImmutableArray<string> Folders =
    [
        "src/Norristown.Core/Syntax/Generated",
        "src/Norristown.Core/Syntax/Nodes/Generated",
        "src/Norristown.Core/Syntax/InternalSyntax/Generated",
        "src/Norristown.Core/Syntax/InternalSyntax/Generated/Nodes",
    ];

    private const string Header =
        "// Generated from " + NodeTable.File + " by scripts/generate-syntax.ps1. Change the table, not this file.";

    /// <summary>Every file the table makes, by its path relative to the repository.</summary>
    /// <param name="nodes">The table.</param>
    /// <returns>The text of each file, keyed by its path with <c>/</c> separators.</returns>
    public static SortedDictionary<string, string> Files(ImmutableArray<NodeRow> nodes)
    {
        var table = new NodeTree(nodes);
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            files[$"src/Norristown.Core/Syntax/Nodes/Generated/{node.Name}.cs"] = NodeFile(table, node);
            if (!node.IsHandWritten)
                files[$"src/Norristown.Core/Syntax/InternalSyntax/Generated/Nodes/{node.Name}.cs"] = GreenFile(table, node);
        }
        files["src/Norristown.Core/Syntax/InternalSyntax/Generated/GreenSyntax.cs"] = CreateRedFile(nodes);
        files["src/Norristown.Core/Syntax/Generated/SyntaxVisitor.cs"] = VisitorFile(nodes, generic: false);
        files["src/Norristown.Core/Syntax/Generated/SyntaxVisitorOfT.cs"] = VisitorFile(nodes, generic: true);

        // The repository is LF throughout, whatever the platform the generator runs on.
        foreach (var path in files.Keys.ToList())
            files[path] = files[path].ReplaceLineEndings("\n");
        return files;
    }

    private static string NodeFile(NodeTree table, NodeRow node)
    {
        var members = new List<string>();
        if (!node.IsHandWritten)
        {
            foreach (var (declarer, slot, index) in table.Layout(node))
            {
                if (!Emits(node, slot))
                    continue;
                if (declarer == node)
                {
                    members.Add(node.IsAbstract
                        ? Declaration(node, slot)
                        : Property(node, slot, index, "", table.Hides(node, slot) ? "new " : ""));
                }
                else if (declarer.IsAbstract)
                {
                    members.Add(Property(node, slot, index, "override "));
                }
            }
            members.AddRange(node.Slots.Where(slot => !slot.IsPiece && Emits(node, slot))
                .Select(slot => Property(node, slot, -1, "")));
        }

        var text = new StringBuilder();
        text.AppendLine(Header);
        if (!node.IsHandWritten)
        {
            if (members.Any(member => member.Contains("ImmutableArray<", StringComparison.Ordinal)))
                text.AppendLine("using System.Collections.Immutable;");
            text.AppendLine("using Norristown.Syntax.InternalSyntax;");
        }
        text.AppendLine();
        text.AppendLine("namespace Norristown.Syntax;");
        text.AppendLine();

        var access = node.IsInternal ? "internal" : "public";
        var sealing = node.IsAbstract ? "abstract " : table.HasHeirs(node) ? "" : "sealed ";
        var partial = node.IsPartial || node.IsHandWritten ? "partial " : "";
        if (!node.IsHandWritten)
            text.Append(Summary(node.Summary, ""));
        text.AppendLine($"{access} {sealing}{partial}class {node.Name} : {node.Base}");
        text.AppendLine("{");

        var ordered = new List<string>();
        if (!node.IsHandWritten)
        {
            var kept = node.Slots
                .Where(slot => Emits(node, slot) && slot.Form is "nodes" or "cache"
                    && !(slot.IsPiece && NodeTree.ReadsSlots(node)))
                .ToList();
            foreach (var slot in kept)
                text.AppendLine($"    private ImmutableArray<{slot.ItemType}> {slot.Field};");
            if (kept.Count > 0)
                text.AppendLine();

            var ctor = node.IsAbstract ? "private protected" : "internal";
            text.AppendLine($"    {ctor} {node.Name}(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)");
            text.AppendLine("        : base(tree, parent, green, position)");
            text.AppendLine("    {");
            text.AppendLine("    }");
            ordered.AddRange(members);
        }

        if (!node.IsAbstract)
        {
            var call = node.HasVisitMethod ? $"visitor.Visit{node.BareName}(this)" : "visitor.DefaultVisit(this)";
            ordered.Add(
                $"    /// <inheritdoc/>\n    public override void Accept(SyntaxVisitor visitor) => {call};\n");
            ordered.Add(
                "    /// <inheritdoc/>\n"
                + "    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>\n"
                + $"        {call};\n");
        }

        for (var i = 0; i < ordered.Count; i++)
        {
            if (i > 0 || !node.IsHandWritten)
                text.AppendLine();
            text.Append(ordered[i]);
        }
        text.AppendLine("}");
        return text.ToString();
    }

    /// <summary>Whether the red class carries <paramref name="slot"/> as a property.</summary>
    private static bool Emits(NodeRow node, NodeSlot slot) => slot.Role switch
    {
        SlotRole.Legacy => !node.IsConverted,
        SlotRole.Member => true,
        _ => slot.IsWritten || node.IsConverted || node.IsUnbuilt,
    };

    /// <summary>The property an abstract class declares for a slot its subclasses place themselves.</summary>
    private static string Declaration(NodeRow node, NodeSlot slot) =>
        Summary(slot.Summary, "    ") + $"    public abstract {Type(node, slot)} {slot.Name} {{ get; }}\n";

    private static string Property(NodeRow node, NodeSlot slot, int index, string overriding, string hiding = "")
    {
        var text = new StringBuilder();
        text.Append(overriding.Length > 0 ? "    /// <inheritdoc/>\n" : Summary(slot.Summary, "    "));
        var declaration = $"    public {overriding}{hiding}{Type(node, slot)} {slot.Name}";

        if (slot.IsPiece && NodeTree.ReadsSlots(node))
        {
            text.AppendLine($"{declaration} => {SlotRead(slot, index)};");
            return text.ToString();
        }

        switch (slot.Form)
        {
            case "nodes":
                text.AppendLine($"{declaration} => Nodes(ref {slot.Field});");
                return text.ToString();
            case "cache":
                text.AppendLine(declaration);
                text.AppendLine("    {");
                text.AppendLine("        get");
                text.AppendLine("        {");
                text.AppendLine($"            if ({slot.Field}.IsDefault)");
                text.AppendLine($"                ImmutableInterlocked.InterlockedInitialize(ref {slot.Field}, {slot.Read});");
                text.AppendLine($"            return {slot.Field};");
                text.AppendLine("        }");
                text.AppendLine("    }");
                return text.ToString();
            default:
                // A slot reads itself when the green node is the typed one and searches when it
                // is the generic node the parser still builds for this kind.
                var body = slot.ReadsEitherWay
                    ? $"Green is GreenSyntax ? {slot.Read} : {SlotRead(slot, index)}"
                    : slot.Read;
                var line = $"{declaration} => {body};";
                text.AppendLine(line.Length <= 120 ? line : $"{declaration} =>\n        {body};");
                return text.ToString();
        }
    }

    /// <summary>The type of the property for <paramref name="slot"/> on <paramref name="node"/>.</summary>
    private static string Type(NodeRow node, NodeSlot slot) =>
        slot.IsPiece && NodeTree.ReadsSlots(node) ? slot.Type : slot.PropertyType;

    /// <summary>How the property reads slot <paramref name="index"/> of a typed green node.</summary>
    private static string SlotRead(NodeSlot slot, int index) => slot.List switch
    {
        ListShape.Nodes => $"SlotList<{slot.ListItemType}>({index})",
        ListShape.Separated => $"SlotSeparatedList<{slot.ListItemType}>({index})",
        ListShape.Tokens => $"SlotTokenList({index})",
        _ when slot.IsToken => slot.IsRequired ? $"SlotToken({index})" : $"SlotTokenOrNull({index})",
        _ => slot.IsRequired
            ? $"SlotNode<{slot.BareType}>({index})"
            : $"SlotNodeOrNull<{slot.BareType}>({index})",
    };

    private static string GreenFile(NodeTree table, NodeRow node)
    {
        var slots = node.IsAbstract ? ImmutableArray<LaidOutSlot>.Empty : table.Layout(node);
        var text = new StringBuilder();
        text.AppendLine(Header);
        if (!node.IsAbstract)
            text.AppendLine("using Red = Norristown.Syntax;");
        text.AppendLine();
        text.AppendLine("namespace Norristown.Syntax.InternalSyntax;");
        text.AppendLine();
        text.Append(Summary(
            [$"The green node of <see cref=\"Norristown.Syntax.{node.Name}\"/>.", .. node.Summary], ""));
        var sealing = node.IsAbstract ? "abstract " : table.HasHeirs(node) ? "" : "sealed ";
        text.AppendLine($"internal {sealing}class {node.Name} : {table.GreenBase(node)}");
        text.AppendLine("{");

        if (node.IsAbstract)
        {
            text.AppendLine($"    private protected {node.Name}(SyntaxKind kind, int fullWidth) : base(kind, fullWidth)");
            text.AppendLine("    {");
            text.AppendLine("    }");
            text.AppendLine("}");
            return text.ToString();
        }

        foreach (var (_, slot, _) in slots)
            text.AppendLine($"    private readonly {table.GreenType(slot)} {slot.Field};");
        if (slots.Length > 0)
            text.AppendLine();

        var parameters = string.Join(", ", slots.Select(s => $"{table.GreenType(s.Slot)} {s.Slot.Field}"));
        var width = slots.Length == 0
            ? "0"
            : string.Join(" + ", slots.Select(s =>
                s.Slot.IsRequired && s.Slot.List == ListShape.None
                    ? $"{s.Slot.Field}.FullWidth"
                    : $"({s.Slot.Field}?.FullWidth ?? 0)"));
        var signature = $"    internal {node.Name}({parameters})";
        var chain = $"        : base(SyntaxKind.{node.Kinds[0]}, {width})";
        text.AppendLine(signature.Length + chain.Length <= 118 && signature.Length <= 118 ? signature : Wrapped(signature));
        text.AppendLine(chain);
        text.AppendLine("    {");
        foreach (var (_, slot, _) in slots)
            text.AppendLine($"        this.{slot.Field} = {slot.Field};");
        text.AppendLine("    }");

        if (table.HasHeirs(node))
        {
            text.AppendLine();
            text.AppendLine($"    private protected {node.Name}(SyntaxKind kind, int fullWidth) : base(kind, fullWidth)");
            text.AppendLine("    {");
            foreach (var (_, slot, _) in slots)
                text.AppendLine($"        {slot.Field} = default!;");
            text.AppendLine("    }");
        }

        if (node.IsMissingNode)
        {
            text.AppendLine();
            text.AppendLine("    /// <inheritdoc/>");
            text.AppendLine("    public override bool IsMissing => true;");
        }

        text.AppendLine();
        text.AppendLine("    /// <inheritdoc/>");
        text.AppendLine($"    public override int SlotCount => {slots.Length};");
        text.AppendLine();
        text.AppendLine("    /// <inheritdoc/>");
        if (slots.Length == 0)
        {
            text.AppendLine("    public override GreenNode? GetSlot(int index) => throw new ArgumentOutOfRangeException(nameof(index));");
        }
        else
        {
            text.AppendLine("    public override GreenNode? GetSlot(int index) => index switch");
            text.AppendLine("    {");
            foreach (var (_, slot, i) in slots)
                text.AppendLine($"        {i} => this.{slot.Field},");
            text.AppendLine("        _ => throw new ArgumentOutOfRangeException(nameof(index)),");
            text.AppendLine("    };");
        }
        text.AppendLine();
        text.AppendLine("    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>");
        text.AppendLine($"        new Red.{node.Name}(tree, parent, this, position);");
        text.AppendLine("}");
        return text.ToString();
    }

    /// <summary>A constructor signature too long for one line, one parameter to a line.</summary>
    private static string Wrapped(string signature)
    {
        var open = signature.IndexOf('(', StringComparison.Ordinal);
        var head = signature[..(open + 1)];
        var parameters = signature[(open + 1)..^1].Split(", ");
        var text = new StringBuilder(head).Append('\n');
        for (var i = 0; i < parameters.Length; i++)
            text.Append("        ").Append(parameters[i]).Append(i < parameters.Length - 1 ? ",\n" : ")");
        return text.ToString();
    }

    private static string CreateRedFile(ImmutableArray<NodeRow> nodes)
    {
        var text = new StringBuilder();
        text.AppendLine(Header);
        text.AppendLine("using Red = Norristown.Syntax;");
        text.AppendLine();
        text.AppendLine("namespace Norristown.Syntax.InternalSyntax;");
        text.AppendLine();
        text.AppendLine("public sealed partial class GreenSyntax");
        text.AppendLine("{");
        text.AppendLine("    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) => Kind switch");
        text.AppendLine("    {");
        foreach (var node in nodes.Where(node => !node.IsHandWritten))
        {
            foreach (var kind in node.Kinds)
                text.AppendLine($"        SyntaxKind.{kind} => new Red.{node.Name}(tree, parent, this, position),");
        }
        text.AppendLine("        _ => throw new InvalidOperationException($\"{Kind} is not the kind of a parsed node\"),");
        text.AppendLine("    };");
        text.AppendLine("}");
        return text.ToString();
    }

    private static string VisitorFile(ImmutableArray<NodeRow> nodes, bool generic)
    {
        var name = generic ? "SyntaxVisitor<TResult>" : "SyntaxVisitor";
        var result = generic ? "TResult?" : "void";
        var text = new StringBuilder();
        text.AppendLine(Header);
        text.AppendLine();
        text.AppendLine("namespace Norristown.Syntax;");
        text.AppendLine();
        text.AppendLine("/// <summary>");
        text.AppendLine("/// Dispatches on what a node is: <see cref=\"Visit\"/> hands a node to the method its class");
        text.AppendLine("/// has here, and every one of those hands it on to <see cref=\"DefaultVisit\"/> unless it is");
        text.AppendLine("/// overridden. Override the nodes a feature is about and leave the rest;");
        text.AppendLine("/// <see cref=\"SyntaxWalker\"/> is the one that goes on down the tree.");
        text.AppendLine("/// </summary>");
        if (generic)
            text.AppendLine("/// <typeparam name=\"TResult\">What visiting a node works out.</typeparam>");
        text.AppendLine($"public abstract class {name}");
        text.AppendLine("{");
        text.AppendLine("    /// <summary>Hands <paramref name=\"node\"/> to the method its class has, and does nothing for null.</summary>");
        text.AppendLine("    /// <param name=\"node\">The node to visit, or null.</param>");
        if (generic)
        {
            text.AppendLine("    /// <returns>What the method for its class worked out, or the default for none.</returns>");
            text.AppendLine("    public virtual TResult? Visit(SyntaxNode? node) => node is null ? default : node.Accept(this);");
        }
        else
        {
            text.AppendLine("    public virtual void Visit(SyntaxNode? node) => node?.Accept(this);");
        }
        text.AppendLine();
        text.AppendLine("    /// <summary>What every method below does unless it is overridden.</summary>");
        text.AppendLine("    /// <param name=\"node\">The node visited.</param>");
        if (generic)
        {
            text.AppendLine("    /// <returns>The default of <typeparamref name=\"TResult\"/>.</returns>");
            text.AppendLine("    public virtual TResult? DefaultVisit(SyntaxNode node) => default;");
        }
        else
        {
            text.AppendLine("    public virtual void DefaultVisit(SyntaxNode node)");
            text.AppendLine("    {");
            text.AppendLine("    }");
        }

        foreach (var node in nodes.Where(node => node.HasVisitMethod))
        {
            text.AppendLine();
            text.AppendLine($"    /// <summary>Visits <see cref=\"{node.Name}\"/>.</summary>");
            text.AppendLine("    /// <param name=\"node\">The node visited.</param>");
            if (generic)
                text.AppendLine("    /// <returns>What visiting it worked out.</returns>");
            text.AppendLine($"    public virtual {result} Visit{node.BareName}({node.Name} node) => DefaultVisit(node);");
        }

        text.AppendLine("}");
        return text.ToString();
    }

    private static string Summary(ImmutableArray<string> summary, string indent)
    {
        if (summary.Length == 0)
            return "";
        if (summary.Length == 1)
            return $"{indent}/// <summary>{summary[0]}</summary>\n";
        var text = new StringBuilder($"{indent}/// <summary>\n");
        foreach (var line in summary)
            text.Append($"{indent}/// {line}\n");
        return text.Append($"{indent}/// </summary>\n").ToString();
    }
}
