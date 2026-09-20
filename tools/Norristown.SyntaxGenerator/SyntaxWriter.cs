using System.Collections.Immutable;
using System.Text;

namespace Norristown.SyntaxGenerator;

/// <summary>
/// Writes the C# the node table describes: a class per node, the switch from kind to class, and
/// the visitors. It builds strings, which is enough for a table this size, and what it makes is
/// checked in, so the tests can ask for it in memory and compare.
/// </summary>
public static class SyntaxWriter
{
    /// <summary>The folders every generated file goes in, relative to the repository.</summary>
    public static readonly ImmutableArray<string> Folders =
    [
        "src/Norristown.Core/Syntax/Generated",
        "src/Norristown.Core/Syntax/Nodes/Generated",
        "src/Norristown.Core/Syntax/InternalSyntax/Generated",
    ];

    private const string Header =
        "// Generated from " + NodeTable.File + " by scripts/generate-syntax.ps1. Change the table, not this file.";

    /// <summary>Every file the table makes, by its path relative to the repository.</summary>
    /// <param name="nodes">The table.</param>
    /// <returns>The text of each file, keyed by its path with <c>/</c> separators.</returns>
    public static SortedDictionary<string, string> Files(ImmutableArray<NodeRow> nodes)
    {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var node in nodes)
            files[$"src/Norristown.Core/Syntax/Nodes/Generated/{node.Name}.cs"] = NodeFile(node);
        files["src/Norristown.Core/Syntax/InternalSyntax/Generated/GreenSyntax.cs"] = CreateRedFile(nodes);
        files["src/Norristown.Core/Syntax/Generated/SyntaxVisitor.cs"] = VisitorFile(nodes, generic: false);
        files["src/Norristown.Core/Syntax/Generated/SyntaxVisitorOfT.cs"] = VisitorFile(nodes, generic: true);

        // The repository is LF throughout, whatever the platform the generator runs on.
        foreach (var path in files.Keys.ToList())
            files[path] = files[path].ReplaceLineEndings("\n");
        return files;
    }

    private static string NodeFile(NodeRow node)
    {
        var text = new StringBuilder();
        text.AppendLine(Header);
        if (!node.IsHandWritten)
        {
            if (node.Slots.Any(slot => slot.ItemType is not null))
                text.AppendLine("using System.Collections.Immutable;");
            text.AppendLine("using Norristown.Syntax.InternalSyntax;");
        }
        text.AppendLine();
        text.AppendLine("namespace Norristown.Syntax;");
        text.AppendLine();

        var access = node.IsInternal ? "internal" : "public";
        var kind = node.IsAbstract ? "abstract" : "sealed";
        var partial = node.IsPartial || node.IsHandWritten ? "partial " : "";
        if (!node.IsHandWritten)
            text.Append(Summary(node.Summary, ""));
        text.AppendLine($"{access} {kind} {partial}class {node.Name} : {node.Base}");
        text.AppendLine("{");

        var members = new List<string>();
        if (!node.IsHandWritten)
        {
            var kept = node.Slots.Where(slot => slot.Form is "nodes" or "cache").ToList();
            foreach (var slot in kept)
                text.AppendLine($"    private ImmutableArray<{slot.ItemType}> {slot.Field};");
            if (kept.Count > 0)
                text.AppendLine();

            var ctor = node.IsAbstract ? "private protected" : "internal";
            text.AppendLine($"    {ctor} {node.Name}(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)");
            text.AppendLine("        : base(tree, parent, green, position)");
            text.AppendLine("    {");
            text.AppendLine("    }");

            members.AddRange(node.Slots.Select(Property));
        }

        if (!node.IsAbstract)
        {
            var call = node.HasVisitMethod ? $"visitor.Visit{node.BareName}(this)" : "visitor.DefaultVisit(this)";
            members.Add(
                $"    /// <inheritdoc/>\n    public override void Accept(SyntaxVisitor visitor) => {call};\n");
            members.Add(
                "    /// <inheritdoc/>\n"
                + "    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>\n"
                + $"        {call};\n");
        }

        for (var i = 0; i < members.Count; i++)
        {
            if (i > 0 || !node.IsHandWritten)
                text.AppendLine();
            text.Append(members[i]);
        }
        text.AppendLine("}");
        return text.ToString();
    }

    private static string Property(NodeSlot slot)
    {
        var text = new StringBuilder();
        text.Append(Summary(slot.Summary, "    "));
        switch (slot.Form)
        {
            case "nodes":
                text.AppendLine($"    public {slot.Type} {slot.Name} => Nodes(ref {slot.Field});");
                break;
            case "cache":
                text.AppendLine($"    public {slot.Type} {slot.Name}");
                text.AppendLine("    {");
                text.AppendLine("        get");
                text.AppendLine("        {");
                text.AppendLine($"            if ({slot.Field}.IsDefault)");
                text.AppendLine($"                ImmutableInterlocked.InterlockedInitialize(ref {slot.Field}, {slot.Read});");
                text.AppendLine($"            return {slot.Field};");
                text.AppendLine("        }");
                text.AppendLine("    }");
                break;
            default:
                var line = $"    public {slot.Type} {slot.Name} => {slot.Read};";
                if (line.Length <= 120)
                    text.AppendLine(line);
                else
                    text.AppendLine($"    public {slot.Type} {slot.Name} =>\n        {slot.Read};");
                break;
        }
        return text.ToString();
    }

    private static string CreateRedFile(ImmutableArray<NodeRow> nodes)
    {
        var text = new StringBuilder();
        text.AppendLine(Header);
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
                text.AppendLine($"        SyntaxKind.{kind} => new {node.Name}(tree, parent, this, position),");
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
