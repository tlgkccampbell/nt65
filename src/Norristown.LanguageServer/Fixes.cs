using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// Turns the quick fixes that the diagnostics in a range carry into edits. Each fix applies what
/// its message already suggests. The fixes include the long branch that reaches the target, the
/// return instruction an interrupt handler must end with, and the nt65 form of a ca65 directive.
/// They also include the declared name closest to a misspelling, the storage a <c>.res</c>
/// reserves, an export wide enough for what it exports, and removing or exporting a declaration
/// nothing refers to. Where a line has two plausible readings, both are offered and neither is
/// preferred, since only the programmer knows which was meant.
/// </summary>
internal static class Fixes
{
    /// <summary>
    /// Returns the fixes for the diagnostics of <paramref name="model"/>'s file on the lines
    /// <paramref name="range"/> covers.
    /// </summary>
    public static IEnumerable<Change> In(ProgramAnalysis analysis, SemanticModel model, Protocol.Range range)
    {
        foreach (var diagnostic in analysis.DiagnosticsFor(model.Tree.Path))
        {
            if (diagnostic.Fix is not { } fix
                || diagnostic.Span.LineIndex < range.Start.Line || diagnostic.Span.LineIndex > range.End.Line)
            {
                continue;
            }
            foreach (var change in For(analysis, model, diagnostic, fix))
                yield return change;
        }
    }

    private static IEnumerable<Change> For(
        ProgramAnalysis analysis, SemanticModel model, Diagnostic diagnostic, DiagnosticFix fix)
    {
        var tree = model.Tree;
        var line = diagnostic.Span.LineIndex;
        switch (fix.Kind)
        {
            case FixKind.EndPath:
                yield return Fix(diagnostic, "End the path here with `.next ?`",
                    [Edits.InsertAfter(tree, line, $"{Edits.IndentOf(tree, line)}.next ?")]);
                break;

            case FixKind.Fallthrough when fix is { Text: { } routine, At: { } closer }:
                var last = closer.LineIndex;
                yield return Fix(diagnostic, $"Add `.fallthrough {routine}`",
                    [new Edit(tree, new TextSpan(tree.LineStarts[last], 0),
                        $"{Edits.IndentOf(tree, last)}    .fallthrough {routine}\n")]);
                break;

            case FixKind.AlwaysTaken when fix is { Text: { } target, At: { } branch }:
                var after = branch.LineIndex;
                yield return Fix(diagnostic, $"Add `.next {target}`: the branch is always taken",
                    [Edits.InsertAfter(tree, after, $"{Edits.IndentOf(tree, after)}.next {target}")]);
                break;

            case FixKind.Mnemonic when fix.Text is { } mnemonic && ReplaceMnemonic(tree, line, mnemonic) is { } call:
                yield return Fix(diagnostic, $"Call with `{mnemonic}`", [call]);
                break;

            case FixKind.Branch when fix.Text is { } longer && ReplaceMnemonic(tree, line, longer) is { } branch:
                yield return Fix(diagnostic, $"Branch with `{longer}`", [branch]);
                break;

            case FixKind.Return when fix.Text is { } leaves && ReplaceMnemonic(tree, line, leaves) is { } returned:
                yield return Fix(diagnostic, $"Leave with `{leaves}`", [returned]);
                break;

            case FixKind.Export when fix is { Text: { } name, At: { } at } && analysis.ModelFor(at.File) is { } declaring:
                yield return Fix(diagnostic, $"Export `{name}` from `{declaring.FileScope.Module}`",
                    [Exported(declaring, name)]);
                break;

            case FixKind.Placed when fix.At is { } at && analysis.ModelFor(at.File) is { } declaring:
                yield return Fix(diagnostic, $"Declare `{declaring.FileScope.Module}` as placed",
                    [new Edit(declaring.Tree, new TextSpan(Edits.SpanOf(declaring.Tree, at).End, 0), ": placed")]);
                break;

            case FixKind.Use when fix.Text is { } path:
                yield return Fix(diagnostic, $"Bring in `{path}` with `.use`", [Used(tree, path)]);
                break;

            case FixKind.State when fix.At is { } label && analysis.ModelFor(label.File) is { } labelled:
                if (StateAfter(analysis, labelled, label) is { } state)
                    yield return Fix(diagnostic, state.Title, state.Edits);
                break;

            case FixKind.DataDeclaration:
                if (DataDeclaration(tree, line) is { } declaration)
                    yield return Fix(diagnostic, declaration.Title, declaration.Edits);
                break;

            case FixKind.Spelling when fix.Text is { } spelled:
                yield return Fix(diagnostic,
                    spelled == "}" ? "Close the block with `}`" : $"Change to `{spelled}`",
                    [new Edit(tree, Edits.SpanOf(tree, diagnostic.Span), spelled)]);
                break;

            case FixKind.NearestName when fix.Text is { } nearest:
                yield return Fix(diagnostic, $"Change it to `{nearest}`",
                    [new Edit(tree, Edits.SpanOf(tree, diagnostic.Span), nearest)]);
                break;

            case FixKind.Rename:
                // No edit is made. Only the programmer can choose the new name, so the editor
                // puts the caret on the name and starts a rename.
                var named = Edits.SpanOf(tree, diagnostic.Span);
                yield return new Change(
                    $"Rename `{tree.Text[named.Start..named.End]}`…",
                    CodeActionKinds.QuickFix,
                    [],
                    diagnostic,
                    Renames: diagnostic.Span);
                break;

            case FixKind.AssertLevel:
                if (WithoutLevel(tree, diagnostic.Span) is { } dropped)
                    yield return Fix(diagnostic, "Drop the level: an assertion that fails is an error", [dropped]);
                break;

            case FixKind.Storage:
                if (Storage(tree, diagnostic.Span) is { } reserved)
                    yield return Fix(diagnostic, $"Declare it as `{reserved.Text}`", [reserved]);
                break;

            case FixKind.DataMember:
                foreach (var change in DataMember(analysis.Program, model, diagnostic))
                    yield return change;
                break;

            case FixKind.ExportSize when fix.Text is { } size:
                if (ExportSize(tree, diagnostic.Span, size) is { } widened)
                    yield return Fix(diagnostic, $"Export it as `{size}`", [widened]);
                break;

            case FixKind.Parentheses:
                foreach (var change in Parenthesized(tree, diagnostic))
                    yield return change;
                break;

            case FixKind.Width when fix.Text is { } item:
                foreach (var width in (int[])[8, 16])
                {
                    yield return Fix(diagnostic, $"Add `.ensure {item}{width}`",
                        [Edits.InsertBefore(tree, line, $".ensure {item}{width}")], preferred: false);
                }
                break;

            case FixKind.Signature when fix is { Text: { } register, At: { } routine }:
                foreach (var width in (int[])[8, 16])
                {
                    if (Edits.SignatureItem(tree, routine.LineIndex, $"{register}{width}") is { } item)
                    {
                        yield return Fix(diagnostic,
                            $"Declare it `{register}{width}`, which is what the routine assumes", [item],
                            preferred: false);
                    }
                }
                break;

            case FixKind.Unused when fix.Text is { } unused:
                foreach (var change in Unused(model, diagnostic, unused))
                    yield return change;
                break;

            case FixKind.UseItem when fix.Text is { } brought:
                if (UseItems.Without(model, brought) is { Count: > 0 } without)
                    yield return Fix(diagnostic, $"Remove the `.use` of `{brought}`", without);
                break;

            case FixKind.Const:
                yield return Fix(diagnostic, "Declare it with `.const`",
                    [new Edit(tree, new TextSpan(Edits.SpanOf(tree, diagnostic.Span).Start, 0), ".const ")]);
                break;

            case FixKind.MissingPiece when fix.Text is { } piece:
                if (Piece(tree, diagnostic, piece) is { } inserted)
                    yield return Fix(diagnostic, $"Insert the missing `{piece}`", [inserted]);
                break;

            default:
                break;
        }
    }

    /// <summary>
    /// Creates a fix for <paramref name="diagnostic"/>, preferred unless it is one of several
    /// readings.
    /// </summary>
    private static Change Fix(Diagnostic diagnostic, string title, IReadOnlyList<Edit> edits, bool preferred = true) =>
        new(title, CodeActionKinds.QuickFix, edits, diagnostic, preferred);

    /// <summary>
    /// Returns an edit that replaces the line's mnemonic with <paramref name="mnemonic"/>, in
    /// upper case where the line has it in upper case, or null for a line that has no mnemonic.
    /// </summary>
    private static Edit? ReplaceMnemonic(SyntaxTree tree, int line, string mnemonic)
    {
        if (LineContext.TokensOf(tree, line).FirstOrDefault(token => token.Kind == SyntaxKind.Mnemonic)
            is not { Text: not null } mnemonicToken)
        {
            return null;
        }
        var cased = mnemonicToken.Text.All(char.IsUpper) ? mnemonic.ToUpperInvariant() : mnemonic;
        return new Edit(tree, new TextSpan(mnemonicToken.Start, mnemonicToken.Text.Length), cased);
    }

    /// <summary>
    /// Returns an edit that inserts <paramref name="piece"/> where the line is missing it. The
    /// position comes from the syntax tree rather than the text. A missing token marks the piece
    /// as absent, and that token's diagnostic span gives where the piece belongs. That is the end
    /// of the last token the line does have, not wherever the missing token sits after a trailing
    /// comment. Returns null when no missing token on the line owns the diagnostic, which means
    /// the diagnostic is not about a missing piece.
    /// </summary>
    private static Edit? Piece(SyntaxTree tree, Diagnostic diagnostic, string piece)
    {
        var line = tree.GetLine(Math.Clamp(diagnostic.Span.LineIndex, 0, tree.LineCount - 1));
        foreach (var token in line.DescendantTokens())
        {
            if (!token.IsMissing || !token.GetDiagnostics().Contains(diagnostic))
                continue;

            // A `{` opens a block and is set off by a space as a word of its own. A closing piece
            // goes directly after what it closes.
            var at = Edits.SpanOf(tree, diagnostic.Span).Start;
            var space = piece == "{" && at > 0 && tree.Text[at - 1] is not (' ' or '\t' or '\n' or '\r') ? " " : "";
            return new Edit(tree, new TextSpan(at, 0), space + piece);
        }
        return null;
    }

    /// <summary>
    /// Returns an edit that adds an <c>.export</c> of <paramref name="name"/> under the
    /// <c>.module</c> of the file that declares it.
    /// </summary>
    private static Edit Exported(SemanticModel declaring, string name) =>
        Edits.InsertAfter(declaring.Tree, Edits.LastLine<ModuleDirectiveSyntax>(declaring.Tree), $".export {name}");

    /// <summary>
    /// Returns an edit that adds a <c>.use</c> of <paramref name="path"/> under the last
    /// <c>.use</c>, or under the <c>.module</c> if there is none.
    /// </summary>
    private static Edit Used(SyntaxTree tree, string path)
    {
        var after = Edits.LastLine<UseDirectiveSyntax>(tree) is var use and >= 0
            ? use
            : Edits.LastLine<ModuleDirectiveSyntax>(tree);
        return Edits.InsertAfter(tree, after, $".use {path}");
    }

    /// <summary>
    /// Returns a fix that adds a <c>.state</c> after a label. The <c>.state</c> gives the
    /// processor state the analysis finds on entry to the line below the label, or the routine's
    /// entry state where the analysis found nothing.
    /// </summary>
    private static (string Title, IReadOnlyList<Edit> Edits)? StateAfter(
        ProgramAnalysis analysis, SemanticModel model, Span label)
    {
        var tree = model.Tree;
        var line = label.LineIndex;
        var symbol = model.Symbols.FirstOrDefault(symbol => symbol.DeclarationSpan == label);
        var block = analysis.FlowFor(tree.Path)?.Regions
            .SelectMany(region => region.Blocks)
            .FirstOrDefault(block => block.Label == symbol && block.On is null);
        var reaching = block is { Steps: [var first, ..] }
            ? analysis.StatesFor(tree.Path)?.AnyBefore(first.Statement)?.Processor
            : null;
        if ((reaching ?? symbol?.Routine?.Signature?.Entry) is not { } state)
            return null;

        var items = Edits.FormatState(state);
        var name = symbol?.DisplayName ?? "the label";
        var title = $"Declare `{name}` with `.state {items}`";

        // A label with a statement after it on its line is split there, because a `.state`
        // declares a label only directly after it.
        var tokens = LineContext.TokensOf(tree, line);
        var colon = tokens.FindIndex(token => token.Kind == SyntaxKind.Colon);
        var body = Edits.BodyIndent(tree, line);
        if (colon >= 0 && colon + 1 < tokens.Count)
        {
            var from = tokens[colon].Start + 1;
            var to = tokens[colon + 1].Start;
            return (title, [new Edit(tree, new TextSpan(from, to - from), $"\n{body}.state {items}\n{body}")]);
        }
        return (title, [Edits.InsertAfter(tree, line, $"{body}.state {items}")]);
    }

    /// <summary>
    /// Returns a fix that rewrites a label outside every routine, together with the data lines
    /// under it, as one <c>.data</c> declaration. A single directive goes on the declaration's
    /// own line, and several go in a block.
    /// </summary>
    private static (string Title, IReadOnlyList<Edit> Edits)? DataDeclaration(SyntaxTree tree, int line)
    {
        var tokens = LineContext.TokensOf(tree, line);
        if (tokens is not [{ Kind: SyntaxKind.Identifier } name, { Kind: SyntaxKind.Colon } colon, ..])
            return null;
        var indent = Edits.IndentOf(tree, line);
        var rest = tokens.Count > 2 ? tree.Text[tokens[2].Start..LineContext.CodeEnd(tree, line)] : null;

        var data = new List<string>();
        var last = line;
        for (var next = line + 1; next < tree.LineCount; next++)
        {
            if (tree.GetLine(next).Statement.Kind != SyntaxKind.DataDirective)
                break;
            data.Add(tree.Text[(tree.LineStarts[next] + Edits.IndentOf(tree, next).Length)..LineContext.CodeEnd(tree, next)]);
            last = next;
        }

        var title = $"Make `{name.Text}` a `.data` declaration";
        if (data.Count == 0 && rest is not null)
        {
            return (title,
                [new Edit(tree, new TextSpan(name.Start, colon.Start + 1 - name.Start), $".data {name.Text}:")]);
        }
        if (data.Count == 1 && rest is null)
        {
            var whole = new TextSpan(name.Start, LineContext.CodeEnd(tree, last) - name.Start);
            return (title, [new Edit(tree, whole, $".data {name.Text}: {data[0]}")]);
        }
        if (data.Count == 0)
            return null;

        var members = (rest is null ? data : [rest, .. data]).Select(member => $"{indent}{Edits.Indent}{member}");
        var block = $".data {name.Text} {{\n{string.Join('\n', members)}\n{indent}}}";
        return (title, [new Edit(tree, new TextSpan(name.Start, LineContext.CodeEnd(tree, last) - name.Start), block)]);
    }

    /// <summary>
    /// Returns an edit that removes ca65's assertion level along with the comma that separated it
    /// from the message, or with the comma before it where the level comes last.
    /// </summary>
    private static Edit? WithoutLevel(SyntaxTree tree, Span at)
    {
        var span = Edits.SpanOf(tree, at);
        var tokens = LineContext.TokensOf(tree, at.LineIndex);
        var level = tokens.FindIndex(token => token.Start == span.Start);
        if (level < 0)
            return null;
        var before = level > 0 && tokens[level - 1].Kind == SyntaxKind.Comma ? tokens[level - 1] : default;
        if (level + 1 < tokens.Count && tokens[level + 1].Kind == SyntaxKind.Comma)
        {
            // The comma before the level is kept, so the edit removes everything from just after
            // that comma through the comma after the level.
            var start = before.Text is null ? span.Start : before.Start + 1;
            return new Edit(tree, new TextSpan(start, tokens[level + 1].Start + 1 - start), "");
        }
        return before.Text is null ? null : new Edit(tree, new TextSpan(before.Start, span.End - before.Start), "");
    }

    /// <summary>
    /// Returns an edit that replaces a declaration's <c>.res n</c> with the <c>.byte[n]</c> that
    /// reserves the same space.
    /// </summary>
    private static Edit? Storage(SyntaxTree tree, Span at)
    {
        var span = Edits.SpanOf(tree, at);
        var text = tree.Text[span.Start..span.End];
        if (!text.StartsWith(".res", StringComparison.OrdinalIgnoreCase))
            return null;
        var count = text[".res".Length..].Trim();
        return count.Length == 0 ? null : new Edit(tree, span, $".byte[{count}]");
    }

    /// <summary>
    /// Returns the fixes for a label in mixed data. One makes the label a member of the data, and
    /// the other makes it a position (<c>@name</c>) in the data.
    /// </summary>
    private static IEnumerable<Change> DataMember(ProgramModel program, SemanticModel model, Diagnostic diagnostic)
    {
        var tree = model.Tree;
        var span = Edits.SpanOf(tree, diagnostic.Span);
        var name = tree.Text[span.Start..span.End];
        var tokens = LineContext.TokensOf(tree, diagnostic.Span.LineIndex);
        var colon = tokens.FindIndex(token => token.Start == span.Start) + 1;

        // A member is a name plus what it holds, so it is offered only when a directive follows
        // the label on the line. A bare name can only be a position.
        if (colon > 0 && colon + 1 < tokens.Count && tokens[colon + 1].Kind == SyntaxKind.Directive)
        {
            yield return Fix(diagnostic, $"Make `{name}` a member of the data",
                [new Edit(tree, new TextSpan(span.Start, 0), ".data ")], preferred: false);
        }

        var symbol = model.Symbols.FirstOrDefault(symbol => symbol.DeclarationSpan == diagnostic.Span);
        IReadOnlyList<Edit> edits = symbol is null
            ? [new Edit(tree, new TextSpan(span.Start, 0), "@")]
            : Edits.Rename(program, symbol, "@" + name);
        yield return Fix(diagnostic, $"Make `{name}` a position, `@{name}`", edits, preferred: false);
    }

    /// <summary>
    /// Returns an edit that replaces the address size an <c>.export</c> gives with
    /// <paramref name="size"/>.
    /// </summary>
    private static Edit? ExportSize(SyntaxTree tree, Span at, string size)
    {
        var span = Edits.SpanOf(tree, at);
        var tokens = LineContext.TokensOf(tree, at.LineIndex);
        var colon = tokens.FindIndex(token => token.Start >= span.Start && token.Kind == SyntaxKind.Colon);
        return colon >= 0 && colon + 1 < tokens.Count
            ? new Edit(tree, new TextSpan(tokens[colon + 1].Start, tokens[colon + 1].Text.Length), size)
            : null;
    }

    /// <summary>
    /// Returns fixes for the two readings of an expression that needs parentheses, which are the
    /// reading the language's precedence would give and the other one. Each fix only inserts an
    /// opening and a closing parenthesis, so nothing else on the line changes.
    /// </summary>
    private static IEnumerable<Change> Parenthesized(SyntaxTree tree, Diagnostic diagnostic)
    {
        var at = Edits.SpanOf(tree, diagnostic.Span).Start;
        if (tree.Root.FindToken(at).Parent is not BinaryExpressionSyntax outer || outer.OperatorToken.Span.Start != at)
            yield break;

        var readings = new List<(int Open, int Close)>();
        if (outer.Right is BinaryExpressionSyntax right)
        {
            readings.Add((right.Span.Start, right.Span.End));
            readings.Add((outer.Span.Start, right.Left.Span.End));
        }
        else if (outer.Left is BinaryExpressionSyntax left)
        {
            readings.Add((left.Span.Start, left.Span.End));
            readings.Add((left.Right.Span.Start, outer.Span.End));
        }
        else if (Rightmost(outer.Left) is { } unary)
        {
            readings.Add((unary.Span.Start, unary.Span.End));
            readings.Add((unary.Operand.Span.Start, outer.Span.End));
        }

        foreach (var (open, close) in readings)
        {
            var grouped = tree.Text[outer.Span.Start..open] + "(" + tree.Text[open..close] + ")"
                + tree.Text[close..outer.Span.End];
            yield return Fix(diagnostic, $"Change to `{grouped.Trim()}`",
                [new Edit(tree, new TextSpan(open, 0), "("), new Edit(tree, new TextSpan(close, 0), ")")],
                preferred: false);
        }
    }

    /// <summary>
    /// Returns the unary expression at the right edge of an operand, which is what a byte
    /// operator applies to, or null if the operand does not end in one.
    /// </summary>
    private static UnaryExpressionSyntax? Rightmost(ExpressionSyntax node)
    {
        while (node is BinaryExpressionSyntax binary)
            node = binary.Right;
        return node as UnaryExpressionSyntax;
    }

    /// <summary>
    /// Returns the fixes for a declaration nothing refers to. One removes it along with its body,
    /// and the other exports it so that another module may refer to it.
    /// </summary>
    private static IEnumerable<Change> Unused(SemanticModel model, Diagnostic diagnostic, string name)
    {
        var tree = model.Tree;
        var line = diagnostic.Span.LineIndex;
        var symbol = model.Symbols.FirstOrDefault(symbol => symbol.DeclarationSpan == diagnostic.Span);

        // Removal deletes whole lines, which is only safe where the line starts with the
        // declaration. A label with an instruction after it shares its line with code.
        var tokens = LineContext.TokensOf(tree, line);
        if (tokens.Count > 0 && tokens[0].Start == tree.LineStarts[line] + Edits.IndentOf(tree, line).Length)
            yield return Fix(diagnostic, $"Remove `{name}`", [Edits.RemoveLines(tree, line, Edits.BlockEnd(tree, line))], preferred: false);

        if (symbol is { IsCheapLocal: false, IsReachableByPath: true } exportable && model.FileScope.Module is { } module)
        {
            yield return Fix(diagnostic, $"Export `{name}` from `{module}`",
                [Exported(model, exportable.QualifiedName)], preferred: false);
        }
    }
}
