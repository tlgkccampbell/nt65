using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// What a file's <c>.use</c> lines bring in, read from the lines themselves: which names each
/// brings in, whether the file uses them, and how the lines read once unused items are removed
/// and the rest sorted.
/// <para>
/// A <c>.use module::*</c> is left alone, because what it brings in depends on what that module
/// exports rather than on anything written here; it is treated like a re-export, as part of
/// an interface rather than something this file uses.
/// </para>
/// </summary>
internal static class UseItems
{
    /// <summary>
    /// The change that rewrites the file's <c>.use</c> lines with unused items removed and the
    /// rest sorted, offered where the caret is on one of them and there is something to change.
    /// </summary>
    public static IEnumerable<Change> Organized(SemanticModel model, int caretLine)
    {
        var tree = model.Tree;
        var lines = Written(model);
        if (!lines.Any(line => line.Index == caretLine))
            yield break;

        // A file with errors may not yet use what it brings in, and an item removed on that
        // evidence is one the programmer would have to write again.
        if (model.Tree.Diagnostics.Any(diagnostic => diagnostic.Severity == Severity.Error)
            || model.Diagnostics.Any(diagnostic => diagnostic.Severity == Severity.Error))
        {
            yield break;
        }

        var kept = new List<string>();
        foreach (var line in lines)
        {
            if (Tidied(model, line) is { } code)
                kept.Add(code);
        }
        kept.Sort(StringComparer.Ordinal);

        var written = lines.Select(line => line.Indent + line.Code + line.Comment).ToList();
        var placed = kept.Select(code => lines[0].Indent + code).ToList();
        if (written.SequenceEqual(placed, StringComparer.Ordinal))
            yield break;

        var edits = new List<Edit> { Edits.RemoveLines(tree, lines[0].Index, lines[0].Index) with { Text = string.Join("\n", placed) + (placed.Count > 0 ? "\n" : "") } };
        foreach (var line in lines.Skip(1))
            edits.Add(Edits.RemoveLines(tree, line.Index, line.Index));
        yield return new Change("Organize the `.use` items", CodeActionKinds.Rewrite, edits);
    }

    /// <summary>
    /// The edits that stop <paramref name="name"/> being brought in: removing the whole line
    /// where it is the line's only item, or just that item from a braced list.
    /// </summary>
    public static IReadOnlyList<Edit> Without(SemanticModel model, string name)
    {
        foreach (var line in Written(model))
        {
            if (!line.Items.Any(item => item.Name == name))
                continue;
            if (line.Items.Count == 1)
                return [Edits.RemoveLines(model.Tree, line.Index, line.Index)];
            return [Rewritten(model.Tree, line, [.. line.Items.Where(item => item.Name != name)])];
        }
        return [];
    }

    /// <summary>
    /// The line with its unused items removed, or null where none of its items is used and the
    /// whole line goes.
    /// </summary>
    private static string? Tidied(SemanticModel model, Line line)
    {
        if (line.Glob)
            return line.Code + line.Comment;
        var kept = line.Items.Where(item => IsNamed(model, item.Name)).ToList();
        if (kept.Count == 0)
            return null;
        return (kept.Count == line.Items.Count ? line.Code : Code(line, kept)) + line.Comment;
    }

    /// <summary>Whether the file writes <paramref name="name"/> anywhere but in the <c>.use</c> that brought it in.</summary>
    private static bool IsNamed(SemanticModel model, string name)
    {
        foreach (var reference in model.References)
        {
            if (reference is { IsDeclaration: false, InUse: false }
                && model.Tree.Text[reference.Span.Start..reference.Span.End] == name
                && !Edits.IsQualified(model.Tree, reference.Span))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The line's code bringing in just <paramref name="items"/>, in braces when there is more than one.</summary>
    private static string Code(Line line, IReadOnlyList<Item> items) =>
        items.Count == 1
            ? $".use {line.Path}::{items[0].Written}"
            : $".use {line.Path}::{{{string.Join(", ", items.Select(item => item.Written))}}}";

    /// <summary>The line written again with <paramref name="items"/> in place of what it had.</summary>
    private static Edit Rewritten(SyntaxTree tree, Line line, IReadOnlyList<Item> items) =>
        new(tree, new TextSpan(tree.LineStarts[line.Index] + line.Indent.Length, line.Code.Length), Code(line, items));

    /// <summary>
    /// Every <c>.use</c> the file writes at its top level, in the order it writes them. A
    /// <c>.export .use</c> is an export rather than something this file uses, and is not among
    /// them.
    /// </summary>
    private static IReadOnlyList<Line> Written(SemanticModel model)
    {
        var tree = model.Tree;
        var lines = new List<Line>();
        foreach (var child in tree.Root.Members)
        {
            if (child is not LineSyntax { Statement: UseDirectiveSyntax { IsExported: false } use } written)
                continue;
            if (Read(tree, written.LineIndex, use) is { } line)
                lines.Add(line);
        }
        return lines;
    }

    /// <summary>
    /// What one <c>.use</c> line says, read off the directive it parsed to: the path it names,
    /// whether it is a <c>::*</c>, and the names it brings in. The line's layout — its indent,
    /// its code and any comment after it — is taken from the raw text.
    /// </summary>
    private static Line? Read(SyntaxTree tree, int index, UseDirectiveSyntax use)
    {
        var indent = Edits.IndentOf(tree, index);
        var start = tree.LineStarts[index];
        var end = LineContext.CodeEnd(tree, index);
        var lineEnd = index + 1 < tree.LineStarts.Length ? tree.LineStarts[index + 1] : tree.Text.Length;
        var code = tree.Text[(start + indent.Length)..end];
        var comment = tree.Text[end..lineEnd].TrimEnd('\r', '\n');

        var path = use.Path.Names;
        if (path.Length == 0)
            return null;
        var named = string.Join("::", path.Select(name => name.Text));

        if (use.StarToken is not null)
            return new Line(index, indent, code, comment, named, true, []);

        if (use.OpenBraceToken is not null)
        {
            var items = use.Items
                .Where(item => !item.Name.IsMissing)
                .Select(item => item.Alias is { IsMissing: false } alias
                    ? new Item(alias.Text, $"{item.Name.Text} as {alias.Text}")
                    : new Item(item.Name.Text, item.Name.Text))
                .ToList();
            return new Line(index, indent, code, comment, named, false, items);
        }

        // `.use a::b`, which brings in `b`, or `.use a::b as c`, which brings in `c`.
        var brought = use.Alias is { IsMissing: false } renamed ? renamed.Text : path[^1].Text;
        return new Line(index, indent, code, comment, named, false, [new Item(brought, brought)]);
    }

    /// <summary>One <c>.use</c> line: where it is, how it reads, and what it brings in.</summary>
    /// <param name="Index">The 0-based line.</param>
    /// <param name="Indent">The whitespace it starts with.</param>
    /// <param name="Code">The line without its indent and without any comment after it.</param>
    /// <param name="Comment">What follows the code on the line, comment and all.</param>
    /// <param name="Path">The path it names, without the names in braces.</param>
    /// <param name="Glob">Whether it is a <c>::*</c>, which brings in whatever a module exports.</param>
    /// <param name="Items">The names it brings in, each as it is written.</param>
    private sealed record Line(
        int Index, string Indent, string Code, string Comment, string Path, bool Glob, IReadOnlyList<Item> Items);

    /// <summary>One name a <c>.use</c> brings in.</summary>
    /// <param name="Name">The name this file writes for it.</param>
    /// <param name="Written">The item as it is written, an <c>as</c> and all.</param>
    private sealed record Item(string Name, string Written);
}
