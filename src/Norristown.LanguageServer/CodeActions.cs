using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// The fixes the diagnostics in a range name, as edits: <c>.next ?</c> after a statement, a call
/// written with the other mnemonic, the missing <c>.export</c> or <c>.use</c>, a <c>.state</c> after
/// a label, and a label with the data under it turned into a <c>.data</c> declaration. Each edit is
/// worked out against the files as they are now.
/// </summary>
internal static class CodeActions
{
    /// <summary>How much deeper a body is indented than the line that opens it, where nothing else says.</summary>
    private const string Indent = "    ";

    /// <summary>The fixes for the diagnostics of <paramref name="model"/>'s file on the lines <paramref name="range"/> covers.</summary>
    public static IReadOnlyList<Protocol.CodeAction> In(ProgramAnalysis analysis, SemanticModel model, Protocol.Range range)
    {
        var actions = new List<Protocol.CodeAction>();
        foreach (var diagnostic in analysis.DiagnosticsFor(model.Tree.Path))
        {
            if (diagnostic.Fix is not { } fix || diagnostic.Span.Line - 1 < range.Start.Line || diagnostic.Span.Line - 1 > range.End.Line)
                continue;
            if (Edit(analysis, model.Tree, diagnostic, fix) is not { } found)
                continue;
            var edits = found.Edits
                .GroupBy(edit => edit.Tree)
                .ToDictionary(
                    group => Lsp.ToUri(group.Key.Path),
                    group => (IReadOnlyList<Protocol.TextEdit>)[.. group.Select(edit =>
                        new Protocol.TextEdit(Lsp.ToRange(edit.Tree, edit.Span), edit.Text))]);
            actions.Add(new Protocol.CodeAction(found.Title, "quickfix", [Lsp.ToDiagnostic(diagnostic)],
                new Protocol.WorkspaceEdit(edits), IsPreferred: true));
        }
        return actions;
    }

    private static (string Title, IReadOnlyList<(SyntaxTree Tree, TextSpan Span, string Text)> Edits)? Edit(
        ProgramAnalysis analysis, SyntaxTree tree, Diagnostic diagnostic, DiagnosticFix fix)
    {
        var line = diagnostic.Span.Line - 1;
        switch (fix.Kind)
        {
            case FixKind.EndPath:
                return ("End the path here with `.next ?`", [InsertAfter(tree, line, $"{IndentOf(tree, line)}.next ?")]);

            case FixKind.Mnemonic when fix.Text is { } mnemonic
                && LineContext.TokensOf(tree, line).FirstOrDefault(token => token.Kind == SyntaxKind.Mnemonic) is { Text: not null } written:
                var spelled = written.Text.All(char.IsUpper) ? mnemonic.ToUpperInvariant() : mnemonic;
                return ($"Call with `{mnemonic}`", [(tree, new TextSpan(written.Start, written.Text.Length), spelled)]);

            case FixKind.Export when fix is { Text: { } name, At: { } at } && analysis.ModelFor(at.File) is { } declaring:
                var module = declaring.FileScope.Module;
                return ($"Export `{name}` from `{module}`",
                    [InsertAfter(declaring.Tree, LastLine(declaring.Tree, SyntaxKind.ModuleDirective), $".export {name}")]);

            case FixKind.Use when fix.Text is { } path:
                var after = LastLine(tree, SyntaxKind.UseDirective) is var use and >= 0 ? use : LastLine(tree, SyntaxKind.ModuleDirective);
                return ($"Bring in `{path}` with `.use`", [InsertAfter(tree, after, $".use {path}")]);

            case FixKind.State when fix.At is { } label && analysis.ModelFor(label.File) is { } labelled:
                return StateAfter(analysis, labelled, label);

            case FixKind.DataDeclaration:
                return DataDeclaration(tree, line);

            default:
                return null;
        }
    }

    /// <summary>
    /// A <c>.state</c> after a label, saying what the analysis finds reaching the line under it,
    /// or what its routine is entered with where it finds nothing.
    /// </summary>
    private static (string, IReadOnlyList<(SyntaxTree, TextSpan, string)>)? StateAfter(
        ProgramAnalysis analysis, SemanticModel model, Span label)
    {
        var tree = model.Tree;
        var line = label.Line - 1;
        var symbol = model.Symbols.FirstOrDefault(symbol => symbol.DeclarationSpan == label);
        var block = analysis.FlowFor(tree.Path)?.Regions
            .SelectMany(region => region.Blocks)
            .FirstOrDefault(block => block.Label == symbol && block.On is null);
        var reaching = block is { Steps: [var first, ..] } ? analysis.StatesFor(tree.Path)?.AnyBefore(first.Statement)?.Processor : null;
        if ((reaching ?? symbol?.Routine?.Signature?.Entry) is not { } state)
            return null;

        var items = string.Join(", ", new[]
        {
            state.A == Width.Unchanged ? null : ProcessorState.Spell("a", state.A),
            state.Index == Width.Unchanged ? null : ProcessorState.Spell("i", state.Index),
            state.E == ProcessorMode.Unchanged ? null : ProcessorState.Spell(state.E),
            state.D.Kind == StateValueKind.Unchanged ? null : state.D.Spell("dp"),
            state.B.Kind == StateValueKind.Unchanged ? null : state.B.Spell("dbr"),
        }.OfType<string>());
        var name = symbol?.DisplayName ?? "the label";
        var title = $"Declare `{name}` with `.state {items}`";

        // A label with a statement after it on its line is split there, because a `.state`
        // declares a label only directly after it.
        var tokens = LineContext.TokensOf(tree, line);
        var colon = tokens.FindIndex(token => token.Kind == SyntaxKind.Colon);
        var body = BodyIndent(tree, line);
        if (colon >= 0 && colon + 1 < tokens.Count)
        {
            var from = tokens[colon].Start + 1;
            var to = tokens[colon + 1].Start;
            return (title, [(tree, new TextSpan(from, to - from), $"\n{body}.state {items}\n{body}")]);
        }
        return (title, [InsertAfter(tree, line, $"{body}.state {items}")]);
    }

    /// <summary>
    /// A label outside every routine and the data under it, as one <c>.data</c> declaration: one
    /// directive on the declaration's own line, and several in its block.
    /// </summary>
    private static (string, IReadOnlyList<(SyntaxTree, TextSpan, string)>)? DataDeclaration(SyntaxTree tree, int line)
    {
        var tokens = LineContext.TokensOf(tree, line);
        if (tokens is not [{ Kind: SyntaxKind.Identifier } name, { Kind: SyntaxKind.Colon } colon, ..])
            return null;
        var indent = IndentOf(tree, line);
        var rest = tokens.Count > 2 ? tree.Text[tokens[2].Start..LineContext.CodeEnd(tree, line)] : null;

        var data = new List<string>();
        var last = line;
        for (var next = line + 1; next < tree.LineStarts.Length; next++)
        {
            if (tree.Statement(next).Kind != SyntaxKind.DataDirective)
                break;
            data.Add(tree.Text[(tree.LineStarts[next] + IndentOf(tree, next).Length)..LineContext.CodeEnd(tree, next)]);
            last = next;
        }

        var title = $"Make `{name.Text}` a `.data` declaration";
        if (data.Count == 0 && rest is not null)
            return (title, [(tree, new TextSpan(name.Start, colon.Start + 1 - name.Start), $".data {name.Text}:")]);
        if (data.Count == 1 && rest is null)
        {
            var whole = new TextSpan(name.Start, LineContext.CodeEnd(tree, last) - name.Start);
            return (title, [(tree, whole, $".data {name.Text}: {data[0]}")]);
        }
        if (data.Count == 0)
            return null;

        var members = (rest is null ? data : [rest, .. data]).Select(member => $"{indent}{Indent}{member}");
        var block = $".data {name.Text} {{\n{string.Join('\n', members)}\n{indent}}}";
        return (title, [(tree, new TextSpan(name.Start, LineContext.CodeEnd(tree, last) - name.Start), block)]);
    }

    /// <summary>A new line after <paramref name="line"/>, or at the top of the file for line -1.</summary>
    private static (SyntaxTree, TextSpan, string) InsertAfter(SyntaxTree tree, int line, string text)
    {
        if (line + 1 < tree.LineStarts.Length)
            return (tree, new TextSpan(tree.LineStarts[line + 1], 0), text + "\n");

        // The last line has no line break after it to put the new line behind.
        return (tree, new TextSpan(tree.Text.Length, 0), (tree.Text.EndsWith('\n') ? "" : "\n") + text + "\n");
    }

    /// <summary>The last line at the top level whose statement is of <paramref name="kind"/>, or -1.</summary>
    private static int LastLine(SyntaxTree tree, SyntaxKind kind)
    {
        var found = -1;
        foreach (var child in tree.Root.ChildNodes)
        {
            if (child.Green is GreenLine && child.Statement?.Kind == kind)
                found = child.LineIndex;
        }
        return found;
    }

    /// <summary>The whitespace a line starts with.</summary>
    private static string IndentOf(SyntaxTree tree, int line)
    {
        var start = tree.LineStarts[line];
        var at = start;
        while (at < tree.Text.Length && tree.Text[at] is ' ' or '\t')
            at++;
        return tree.Text[start..at];
    }

    /// <summary>How the lines under a label are indented: as the next line with code is, when it is deeper.</summary>
    private static string BodyIndent(SyntaxTree tree, int line)
    {
        var own = IndentOf(tree, line);
        for (var next = line + 1; next < tree.LineStarts.Length; next++)
        {
            if (LineContext.TokensOf(tree, next).Count == 0)
                continue;
            var indent = IndentOf(tree, next);
            return indent.Length > own.Length ? indent : own + Indent;
        }
        return own + Indent;
    }
}
