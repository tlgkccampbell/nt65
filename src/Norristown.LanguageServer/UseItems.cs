using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// What a file's <c>.use</c> items bring in, read off the lines that write them: which names
/// each brings, whether anything writes them, and how the set of them reads once what nothing
/// names is gone and the rest are in order.
/// <para>
/// A <c>.use module::*</c> is left alone, because what it brings in depends on what that module
/// exports rather than on anything written here, and so is a re-export, which is an interface
/// rather than something this file uses.
/// </para>
/// </summary>
internal static class UseItems
{
    /// <summary>
    /// The file's <c>.use</c> lines, with what nothing names taken out and the rest in order,
    /// offered where the caret is on one of them and there is something to change.
    /// </summary>
    public static IEnumerable<Change> Organized(SemanticModel model, int caretLine)
    {
        var tree = model.Tree;
        var lines = Written(model);
        if (!lines.Any(line => line.Index == caretLine))
            yield break;

        // A file with something wrong with it may not name what it brought in yet, and an item
        // taken away on that evidence is one the programmer has to write again.
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
    /// The edits that stop <paramref name="name"/> being brought in: the line that brings it
    /// only, or the one item of a braced line that does.
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
    /// The line as it reads once the items nothing names are gone, or null where nothing names
    /// any of them and the line itself goes.
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

    /// <summary>The line's code with <paramref name="items"/> in its braces.</summary>
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
            if (child is not LineSyntax { Statement: UseDirectiveSyntax { IsExported: false } } written)
                continue;
            if (Read(tree, written.LineIndex) is { } line)
                lines.Add(line);
        }
        return lines;
    }

    /// <summary>What one <c>.use</c> line says, read off its tokens.</summary>
    private static Line? Read(SyntaxTree tree, int index)
    {
        var tokens = LineContext.TokensOf(tree, index);
        if (tokens is not [{ Kind: SyntaxKind.Directive }, ..])
            return null;
        var indent = Edits.IndentOf(tree, index);
        var start = tree.LineStarts[index];
        var end = LineContext.CodeEnd(tree, index);
        var lineEnd = index + 1 < tree.LineStarts.Length ? tree.LineStarts[index + 1] : tree.Text.Length;
        var code = tree.Text[(start + indent.Length)..end];
        var comment = tree.Text[end..lineEnd].TrimEnd('\r', '\n');

        // The path runs to the `::` before a `*` or a `{`, or to the end of the path itself.
        var at = 1;
        var path = new List<string>();
        while (at < tokens.Count && LineContext.IsWord(tokens[at].Kind))
        {
            path.Add(tokens[at].Text);
            at++;
            if (at >= tokens.Count || tokens[at].Kind != SyntaxKind.ColonColon)
                break;
            at++;
            if (at < tokens.Count && tokens[at].Kind is SyntaxKind.Star or SyntaxKind.OpenBrace)
                break;
        }
        if (path.Count == 0)
            return null;

        if (at < tokens.Count && tokens[at].Kind == SyntaxKind.Star)
            return new Line(index, indent, code, comment, string.Join("::", path), true, []);

        if (at < tokens.Count && tokens[at].Kind == SyntaxKind.OpenBrace)
        {
            var items = new List<Item>();
            for (at++; at < tokens.Count && tokens[at].Kind != SyntaxKind.CloseBrace; at++)
            {
                if (!LineContext.IsWord(tokens[at].Kind))
                    continue;
                var name = tokens[at].Text;
                var written = name;
                if (at + 2 < tokens.Count && tokens[at + 1].Text.Equals("as", StringComparison.OrdinalIgnoreCase))
                {
                    written = $"{name} as {tokens[at + 2].Text}";
                    name = tokens[at + 2].Text;
                    at += 2;
                }
                items.Add(new Item(name, written));
            }
            return new Line(index, indent, code, comment, string.Join("::", path), false, items);
        }

        // `.use a::b`, which brings in `b`, or `.use a::b as c`, which brings in `c`.
        var brought = path[^1];
        if (at + 1 < tokens.Count && tokens[at].Text.Equals("as", StringComparison.OrdinalIgnoreCase))
            brought = tokens[at + 1].Text;
        return new Line(index, indent, code, comment, string.Join("::", path), false, [new Item(brought, brought)]);
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
