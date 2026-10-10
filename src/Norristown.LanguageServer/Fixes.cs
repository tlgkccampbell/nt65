using System.Text;
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
    /// <summary>The register widths a fix may declare, one fix for each.</summary>
    private static readonly int[] Widths = [8, 16];

    /// <summary>
    /// Returns the fixes for the diagnostics of <paramref name="model"/>'s file on the lines
    /// <paramref name="range"/> covers.
    /// </summary>
    public static IEnumerable<Change> In(ProgramAnalysis analysis, SemanticModel model, Protocol.Range range)
    {
        var path = model.Tree.Path;
        foreach (var diagnostic in analysis.DiagnosticsFor(path).Concat(analysis.SuggestionsFor(path)))
        {
            if (diagnostic.Span.LineIndex < range.Start.Line || diagnostic.Span.LineIndex > range.End.Line)
                continue;

            // A diagnostic with a second fix has two readings, and neither is preferred. A fix
            // that relies on something nothing promises says so in its title.
            var readings = diagnostic.Also is null;
            foreach (var fix in new[] { diagnostic.Fix, diagnostic.Also }.OfType<DiagnosticFix>())
            {
                foreach (var found in For(analysis, model, diagnostic, fix))
                {
                    var change = fix.Caveat is { } caveat ? found with { Title = found.Title + caveat } : found;
                    yield return readings ? change : change with { Preferred = false };
                }
            }

            // Any warning that `.allow` may hide can be allowed where it is reported, including one
            // the project raises to an error. That records a decision rather than fixing anything,
            // so it is never preferred.
            if (Catalogue.IsAllowable(diagnostic.Id))
            {
                yield return Fix(diagnostic, $"Allow `{diagnostic.Id}` here with `.allow`",
                    [Edits.InsertBefore(model.Tree, diagnostic.Span.LineIndex, $".allow \"{diagnostic.Id}\"")],
                    preferred: false);
            }
        }
    }

    /// <summary>
    /// Returns the changes that apply one fix a diagnostic carries, which are usually one and
    /// sometimes two readings of the line. A fix whose edit cannot be found in the file as it
    /// stands yields nothing.
    /// </summary>
    private static IEnumerable<Change> For(
        ProgramAnalysis analysis, SemanticModel model, Diagnostic diagnostic, DiagnosticFix fix)
    {
        var tree = model.Tree;
        var line = diagnostic.Span.LineIndex;
        switch (fix.Kind)
        {
            case FixKind.EndPath:
                yield return Fix(diagnostic, "Add `.next ?`: control goes somewhere unnamed",
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

            case FixKind.LandingLabel when fix.Text is { } label && BranchTarget(tree, diagnostic) is { } target:
                yield return Fix(diagnostic, fix.At is null ? $"Branch to `{label}`" : $"Label the landing `{label}` and branch to it",
                    fix.At is { } landing
                        ? [new Edit(tree, target.Span, label), LabelBefore(tree, landing.LineIndex, label)]
                        : [new Edit(tree, target.Span, label)]);
                break;

            case FixKind.Immediate when fix.Text is { } hex:
                var number = Edits.SpanOf(tree, diagnostic.Span);
                var written = Text(tree, number);
                yield return Fix(diagnostic, $"Make it the number `#{written}`",
                    [new Edit(tree, new TextSpan(number.Start, 0), "#")]);
                yield return Fix(diagnostic, $"Write the address as `{hex}`", [new Edit(tree, number, hex)],
                    preferred: false);
                break;

            case FixKind.TailCall when fix.Text is { } jump && ReplaceMnemonic(tree, line, jump) is { } jumped:
                yield return Fix(diagnostic, $"Jump with `{jump}` as a tail call",
                    fix.At is { } leaving ? [jumped, Removed(tree, leaving)] : [jumped]);
                break;

            case FixKind.Redundant:
                yield return Fix(diagnostic, "Remove it", [Removed(tree, diagnostic.Span)]);
                break;

            case FixKind.AddressData when AsData(tree, diagnostic) is var (constant, asData):
                yield return Fix(diagnostic, "Declare it as data with `.data`",
                    [new Edit(tree, constant, $".data {asData}")]);
                yield return Fix(diagnostic, "Declare it as a hardware register with `.mmio`",
                    [new Edit(tree, constant, $".mmio {asData}")], preferred: false);
                break;

            case FixKind.Instruction when fix.Text is { } replacement:
                var replaced = Edits.SpanOf(tree, diagnostic.Span);
                var shouted = Text(tree, replaced).TakeWhile(char.IsLetter).All(char.IsUpper);
                yield return Fix(diagnostic, $"Change it to `{replacement}`",
                    [new Edit(tree, replaced, shouted ? replacement.ToUpperInvariant() : replacement)]);
                break;

            case FixKind.Variant when fix.Text is { } variant:
                yield return Fix(diagnostic, $"List the variant with `as {variant}`",
                    [new Edit(tree, new TextSpan(Edits.SpanOf(tree, diagnostic.Span).End, 0), $" as {variant}")]);
                break;

            case FixKind.PatchTarget when fix is { Text: { } label, At: { } target }:
                yield return Fix(diagnostic, $"Name `{label}` in the `.patch` instead",
                    [new Edit(tree, Edits.SpanOf(tree, target), label)]);
                break;

            case FixKind.PatchTargetAdded when fix.Text is { } label:
                yield return Fix(diagnostic, $"Add `.patch {label}`",
                    [Edits.InsertAfter(tree, line, $"{Edits.IndentOf(tree, line)}.patch {label}")]);
                break;

            case FixKind.Flags when fix.Text is { } flags:
                yield return Fix(diagnostic, $"Change it to `#{flags}`",
                    [new Edit(tree, Edits.SpanOf(tree, diagnostic.Span), flags)]);
                break;

            case FixKind.CarryFolded when fix is { Text: { } folded, At: { } setup }:
                yield return Fix(diagnostic, $"Fold the carry into `#{folded}`",
                    [new Edit(tree, Edits.SpanOf(tree, diagnostic.Span), folded), Removed(tree, setup)]);
                break;

            case FixKind.BranchOver when fix is { Text: { } branch, At: { } jump }:
                yield return Fix(diagnostic, $"Branch with `{branch}`", BranchedOver(model, diagnostic, branch, jump));
                break;

            case FixKind.Mnemonic when fix.Text is { } mnemonic && ReplaceMnemonic(tree, line, mnemonic) is { } call:
                yield return Fix(diagnostic, mnemonic is "jsr" or "jsl" ? $"Call with `{mnemonic}`" : $"Jump with `{mnemonic}`", [call]);
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

            case FixKind.State when fix.At is { } label && analysis.ModelFor(label.File) is { } labeled:
                foreach (var state in StatesAfter(analysis, labeled, label))
                    yield return Fix(diagnostic, state.Title, state.Edits, preferred: false);
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
                    $"Rename `{Text(tree, named)}`…",
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
                foreach (var width in Widths)
                {
                    yield return Fix(diagnostic, $"Add `.ensure {item}{width}`",
                        [Edits.InsertBefore(tree, line, $".ensure {item}{width}")], preferred: false);
                }
                break;

            case FixKind.Signature when fix is { Text: { } register, At: { } routine }:
                foreach (var width in Widths)
                {
                    if (Edits.SignatureItem(tree, routine.LineIndex, $"{register}{width}") is { } item)
                    {
                        yield return Fix(diagnostic,
                            $"Declare it `{register}{width}`, which is what the routine assumes", [item],
                            preferred: false);
                    }
                }
                break;

            case FixKind.Interrupt when fix is { At: { } routine }
                && Edits.SignatureItem(tree, routine.LineIndex, "interrupt") is { } marked:
                yield return Fix(diagnostic, "Mark the routine `interrupt`", [marked]);
                break;

            case FixKind.Reads when fix is { Text: { } register, At: { } routine }
                && Edits.AddedRegister(tree, routine.LineIndex, "reads", register) is { } added:
                yield return Fix(diagnostic, $"Add `{register}` to `reads`", [added]);
                break;

            case FixKind.SaveAround when fix.Text is { } push:
                var pull = "pl" + push[2..];
                var upper = LineContext.TokensOf(tree, line).FirstOrDefault(token => token.Kind == SyntaxKind.Mnemonic)
                    is { Text: { } called } && called.All(char.IsUpper);
                if (upper)
                    (push, pull) = (push.ToUpperInvariant(), pull.ToUpperInvariant());
                yield return Fix(diagnostic, $"Save it around the call with `{push}` and `{pull}`",
                    [Edits.InsertBefore(tree, line, push), Edits.InsertAfter(tree, line, $"{Edits.IndentOf(tree, line)}{pull}")]);
                break;

            case FixKind.Keeps when fix is { Text: { } kept, At: { } callee }
                && analysis.ModelFor(callee.File) is { } declaring
                && Edits.AddedRegister(declaring.Tree, callee.LineIndex, "keeps", kept) is { } promise:
                yield return Fix(diagnostic, $"Add `{kept}` to the `keeps` of `{Text(declaring.Tree, Edits.SpanOf(declaring.Tree, callee))}`", [promise]);
                break;

            case FixKind.Unkeep when fix is { Text: { } unpromised, At: { } jumper }
                && analysis.ModelFor(jumper.File) is { } owning
                && WithoutKept(owning.Tree, jumper.LineIndex, unpromised) is { } unkept:
                yield return Fix(diagnostic, $"Remove `{unpromised}` from the `keeps` of `{Text(owning.Tree, Edits.SpanOf(owning.Tree, jumper))}`", [unkept]);
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

            case FixKind.BankPush when fix.At is { } at:
                yield return fix.Text is { } pushed
                    ? Fix(diagnostic, $"Push the bank with `{pushed}`", [Edits.InsertBefore(tree, at.LineIndex, pushed)])
                    : Fix(diagnostic, "Remove the `phk`", [Removed(tree, at)]);
                break;

            case FixKind.Item:
                if (WithoutItem(tree, diagnostic, fix.Text) is { } removal)
                    yield return Fix(diagnostic, removal.Title, [removal.Edit]);
                break;

            case FixKind.ToEntry:
                if (MovedToEntry(tree, diagnostic) is { } moved)
                    yield return Fix(diagnostic, moved.Title, [moved.Edit]);
                break;

            case FixKind.SegmentBlock:
                if (Unwrapped(tree, line) is { } unwrapped)
                    yield return Fix(diagnostic, "Remove the block and keep its contents", [unwrapped]);
                break;

            case FixKind.FarBranch:
                foreach (var change in FarBranch(model, diagnostic, fix.Text))
                    yield return change;
                break;

            case FixKind.Ensure when fix.Text is { } ensured:
                yield return Fix(diagnostic, $"Add `.ensure {ensured}`", [Edits.InsertBefore(tree, line, $".ensure {ensured}")]);
                break;

            case FixKind.Exit when fix is { Text: { } leaves, At: { } routine }
                && DeclaredAt(model, routine) is { } returning
                && Edits.ExitItem(tree, routine.LineIndex, leaves, (returning.Signature ?? Signature.Default).Entry) is { } exit:
                yield return Fix(diagnostic, $"Declare that `{returning.Name}` returns with `{leaves}`", [exit]);
                break;

            case FixKind.Inferred when fix is { Text: { } items, At: { } routine }
                && DeclaredAt(model, routine) is { } declaring
                && Edits.DeclaredItems(tree, routine.LineIndex, Items(items, before: true), Items(items, before: false)) is { } declared:
                yield return Fix(diagnostic, $"Declare `{items}` in the signature of `{declaring.Name}`", [declared]);
                break;

            case FixKind.StateItem when fix.Text is { } found:
                yield return Fix(diagnostic, $"Change it to `{found}`",
                    [new Edit(tree, Edits.SpanOf(tree, diagnostic.Span), found)]);
                break;

            default:
                break;
        }
    }

    /// <summary>
    /// Returns an edit that removes the item a diagnostic reports from its list, and a title that
    /// says what goes. Where <paramref name="register"/> is given and the item names other
    /// registers too, only that register goes. A <c>.state</c> or <c>.ensure</c> left with no
    /// items goes whole. Returns null where removing the item would leave a signature with an
    /// empty list.
    /// </summary>
    private static (string Title, Edit Edit)? WithoutItem(SyntaxTree tree, Diagnostic diagnostic, string? register)
    {
        if (ItemAt(tree, diagnostic) is not { } item)
            return null;
        if (register is not null && item is StateRegistersItemSyntax { Registers: { Count: > 1 } registers })
        {
            var index = IndexOf(registers, node => node.Name.Text.Equals(register, StringComparison.OrdinalIgnoreCase));
            return index < 0
                ? null
                : ($"Remove `{register}` from `{Text(tree, item.Span)}`", new Edit(tree, ElementSpan(registers, index), ""));
        }
        if (item.Parent is not StateListSyntax list)
            return null;
        var title = $"Remove `{Text(tree, item.Span)}`";
        if (list.Items.Count > 1)
            return (title, new Edit(tree, ElementSpan(list.Items, IndexOf(list.Items, node => node.Span == item.Span)), ""));
        return list.Parent is StateListDirectiveSyntax directive
            ? (title, Removed(tree, tree.GetSpan(directive.Span)))
            : null;
    }

    /// <summary>
    /// Returns an edit that removes <paramref name="register"/> from the <c>keeps</c> item in the
    /// signature of the routine <paramref name="line"/> opens. The item goes whole where it names
    /// nothing else, and the signature goes whole where the item was all it held. Returns null
    /// where the signature has no such item, or where removing it would leave an empty entry
    /// before a <c>-&gt;</c>.
    /// </summary>
    private static Edit? WithoutKept(SyntaxTree tree, int line, string register)
    {
        if (Edits.RoutineHead(tree, line) is not ({ } signature, _)
            || signature.Entry.Items.OfType<StateRegistersItemSyntax>()
                .FirstOrDefault(each => each.Name.Text.Equals("keeps", StringComparison.OrdinalIgnoreCase)) is not { } keeps)
        {
            return null;
        }
        var index = IndexOf(keeps.Registers, node => node.Name.Text.Equals(register, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            return null;
        if (keeps.Registers.Count > 1)
            return new Edit(tree, ElementSpan(keeps.Registers, index), "");
        var items = signature.Entry.Items;
        if (items.Count > 1)
            return new Edit(tree, ElementSpan(items, IndexOf(items, node => node.Span == keeps.Span)), "");
        if (signature.Exit is not null)
            return null;

        // The signature held nothing else, so it goes from the end of the name it follows.
        var start = signature.ColonToken.GetPreviousToken()?.Span.End ?? signature.Span.Start;
        return new Edit(tree, new TextSpan(start, signature.Span.End - start), "");
    }

    /// <summary>
    /// Returns an edit that moves the item a diagnostic reports from after a signature's
    /// <c>-&gt;</c> to the end of its entry, and the title for it. The <c>-&gt;</c> goes too when
    /// the item was the only one after it.
    /// </summary>
    private static (string Title, Edit Edit)? MovedToEntry(SyntaxTree tree, Diagnostic diagnostic)
    {
        if (ItemAt(tree, diagnostic) is not { Parent: StateListSyntax exit } item)
            return null;
        var entry = exit.Parent switch
        {
            ProcSignatureSyntax proc when proc.Exit?.Span == exit.Span => proc.Entry,
            ImportSignatureSyntax import when import.Exit?.Span == exit.Span => import.Entry,
            _ => null,
        };
        if (entry is null)
            return null;

        // One edit runs from the end of the entry to the end of the exit, so that the item can
        // join the one and leave the other without two edits meeting at the same place.
        var start = entry.Span.End;
        var rest = "";
        if (exit.Items.Count > 1)
        {
            var removed = ElementSpan(exit.Items, IndexOf(exit.Items, node => node.Span == item.Span));
            rest = tree.Text[start..removed.Start] + tree.Text[removed.End..exit.Span.End];
        }
        var written = Text(tree, item.Span);
        var separator = entry.Items.Count > 0 ? ", " : "";
        return ($"Move `{written}` before `->`",
            new Edit(tree, new TextSpan(start, exit.Span.End - start), separator + written + rest));
    }

    /// <summary>Returns the signature or <c>.state</c> item at the span a diagnostic reports.</summary>
    private static StateItemSyntax? ItemAt(SyntaxTree tree, Diagnostic diagnostic)
    {
        var span = Edits.SpanOf(tree, diagnostic.Span);
        return tree.Root.DescendantNodes().OfType<StateItemSyntax>().FirstOrDefault(item => item.Span == span);
    }

    /// <summary>
    /// Returns the span to remove to take the element at <paramref name="index"/> out of
    /// <paramref name="list"/>, together with the separator that goes with it. That is the one
    /// after it, or the one before it for the last element.
    /// </summary>
    private static TextSpan ElementSpan<T>(SeparatedSyntaxList<T> list, int index) where T : SyntaxNode
    {
        var (start, end) = index + 1 < list.Count
            ? (list[index].Span.Start, list[index + 1].Span.Start)
            : (list[index - 1].Span.End, list[index].Span.End);
        return new TextSpan(start, end - start);
    }

    /// <summary>Returns the index of the first element of <paramref name="list"/> that matches, or -1.</summary>
    private static int IndexOf<T>(SeparatedSyntaxList<T> list, Func<T, bool> match) where T : SyntaxNode
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (match(list[i]))
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Returns an edit that removes the segment block <paramref name="line"/> opens, both its
    /// opening line and the line that closes it. The lines between move out by one level. Returns
    /// null when the closing brace shares its line with anything else.
    /// </summary>
    private static Edit? Unwrapped(SyntaxTree tree, int line)
    {
        var end = Edits.BlockEnd(tree, line);
        if (end == line || LineContext.TokensOf(tree, end) is not [{ Kind: SyntaxKind.CloseBrace }])
            return null;
        var own = Edits.IndentOf(tree, line);
        var body = Edits.BodyIndent(tree, line);
        var kept = new StringBuilder();
        foreach (var inner in Enumerable.Range(line + 1, end - line - 1))
        {
            var text = tree.Text[tree.LineStarts[inner]..tree.LineStarts[inner + 1]];
            kept.Append(text.StartsWith(body, StringComparison.Ordinal) ? own + text[body.Length..] : text);
        }
        var start = tree.LineStarts[line];
        return new Edit(tree, new TextSpan(start, tree.GetLineEnd(end) - start), kept.ToString());
    }

    /// <summary>
    /// Returns the fix for a branch to a far routine, which only a <c>jml</c> can reach. An
    /// unconditional branch becomes the <c>jml</c>. A conditional one becomes
    /// <paramref name="opposite"/>, which skips over a <c>jml</c> to the routine on the next line
    /// and lands on a new cheap local label after it.
    /// </summary>
    private static IEnumerable<Change> FarBranch(SemanticModel model, Diagnostic diagnostic, string? opposite)
    {
        var tree = model.Tree;
        var line = diagnostic.Span.LineIndex;
        if (Edits.StatementOn(tree, line) is not InstructionStatementSyntax { Operand: { } operand })
            yield break;
        var target = Text(tree, operand.Span);
        if (opposite is null)
        {
            if (ReplaceMnemonic(tree, line, "jml") is { } jumped)
                yield return Fix(diagnostic, "Jump with `jml`", [jumped]);
            yield break;
        }
        if (ReplaceMnemonic(tree, line, opposite) is not { } branched)
            yield break;
        var skip = SkipLabel(model);
        yield return Fix(diagnostic, $"Branch with `{opposite}` around a `jml {target}`",
        [
            branched,
            new Edit(tree, operand.Span, "@" + skip),
            Edits.InsertAfter(tree, line, $"{Edits.IndentOf(tree, line)}jml {target}\n@{skip}:"),
        ]);
    }

    /// <summary>
    /// Returns a name for a cheap local label that skips over a jump, one that no cheap local in
    /// the file already has.
    /// </summary>
    private static string SkipLabel(SemanticModel model)
    {
        var taken = model.Symbols.Where(symbol => symbol.IsCheapLocal).Select(symbol => symbol.Name).ToHashSet();
        var name = "skip";
        for (var number = 2; taken.Contains(name); number++)
            name = $"skip{number}";
        return name;
    }

    /// <summary>Returns the text of <paramref name="span"/> in <paramref name="tree"/>.</summary>
    private static string Text(SyntaxTree tree, TextSpan span) => tree.Text[span.Start..span.End];

    /// <summary>
    /// Returns the symbol whose declaration a diagnostic or a fix reports at <paramref name="at"/>,
    /// or null where nothing in the file is declared there.
    /// </summary>
    private static Symbol? DeclaredAt(SemanticModel model, Span at) =>
        model.Symbols.FirstOrDefault(symbol => symbol.DeclarationSpan == at);

    /// <summary>
    /// Returns the items that a signature written as <paramref name="items"/> gives before its
    /// <c>-&gt;</c>, where <paramref name="before"/> is true, or after it otherwise.
    /// </summary>
    private static List<string> Items(string items, bool before)
    {
        var halves = items.Split(" -> ");
        var half = before ? halves[0] : halves.Length > 1 ? halves[1] : "";
        return [.. half.Split(", ", StringSplitOptions.RemoveEmptyEntries)];
    }

    /// <summary>
    /// Returns the span of the <c>.const</c> declaration a diagnostic names, from its keyword to the
    /// end of its value, and the rest of it as data found elsewhere, such as <c>BORDER: .byte = $D020</c>.
    /// What stands before the keyword, such as <c>.export</c>, and the comment after it are kept.
    /// </summary>
    private static (TextSpan Declaration, string Written)? AsData(SyntaxTree tree, Diagnostic diagnostic)
    {
        var name = Edits.SpanOf(tree, diagnostic.Span);
        if (tree.Root.DescendantNodes().OfType<ConstantDeclarationSyntax>()
            .FirstOrDefault(declaration => declaration.Name.Span == name) is not { } constant)
        {
            return null;
        }
        var start = constant.Keyword.Span.Start;
        var value = Text(tree, constant.Value.Span);
        return (new TextSpan(start, constant.Value.Span.End - start), $"{constant.Name.Text}: .byte = {value}");
    }

    /// <summary>
    /// Creates a quick fix for <paramref name="diagnostic"/>, preferred unless it is one of
    /// several readings of the same line.
    /// </summary>
    private static Change Fix(Diagnostic diagnostic, string title, IReadOnlyList<Edit> edits, bool preferred = true) =>
        new(title, CodeActionKinds.QuickFix, edits, diagnostic, preferred);

    /// <summary>
    /// Returns the target expression of the branch a diagnostic is reported at, or null where
    /// no branch with an operand stands there. The target is the operand's last expression, which
    /// is also the second of <c>bbr0 flags, *+5</c>.
    /// </summary>
    private static ExpressionSyntax? BranchTarget(SyntaxTree tree, Diagnostic diagnostic)
    {
        var span = Edits.SpanOf(tree, diagnostic.Span);
        return Edits.StatementOn(tree, diagnostic.Span.LineIndex) is InstructionStatementSyntax { Operand: { } operand } statement
            && statement.Span.Start == span.Start
                ? operand.ChildNodes.OfType<ExpressionSyntax>().LastOrDefault()
                : null;
    }

    /// <summary>
    /// Returns an edit that declares <paramref name="label"/> on a line of its own before
    /// <paramref name="line"/>, at the routine's margin, one level out from the instruction there,
    /// which is where the formatter puts a cheap local.
    /// </summary>
    private static Edit LabelBefore(SyntaxTree tree, int line, string label)
    {
        var indent = Edits.IndentOf(tree, line);
        var margin = indent.EndsWith(Edits.Indent, StringComparison.Ordinal) ? indent[..^Edits.Indent.Length] : "";
        return new Edit(tree, new TextSpan(tree.LineStarts[line], 0), $"{margin}{label}:\n");
    }

    /// <summary>
    /// Returns the edits that make a branch over a <c>jmp</c> into the one branch
    /// <paramref name="branch"/>. The <c>jmp</c> at <paramref name="jump"/> goes, and so does the
    /// label after it where it stands on a line of its own and only the branch names it.
    /// </summary>
    private static List<Edit> BranchedOver(SemanticModel model, Diagnostic diagnostic, string branch, Span jump)
    {
        var tree = model.Tree;
        var edits = new List<Edit> { new(tree, Edits.SpanOf(tree, diagnostic.Span), branch), Removed(tree, jump) };
        var line = jump.LineIndex + 1;
        while (Edits.StatementOn(tree, line) is BlankLineSyntax)
            line++;
        if (Edits.StatementOn(tree, line) is LabeledLineSyntax { Statement: null } labeled
            && model.SymbolAt(labeled.Label.Name) is { } skip
            && model.ReferencesTo(skip).Count(reference => !reference.IsDeclaration) == 1)
        {
            edits.Add(Edits.RemoveLines(tree, line, line));
        }
        return edits;
    }

    /// <summary>
    /// Returns an edit that removes the statement at <paramref name="at"/>. A statement alone on
    /// its line goes with the whole line, and one after a label leaves the label.
    /// </summary>
    private static Edit Removed(SyntaxTree tree, Span at)
    {
        var span = Edits.SpanOf(tree, at);
        var line = at.LineIndex;
        return span.Start == tree.LineStarts[line] + Edits.IndentOf(tree, line).Length
            ? Edits.RemoveLines(tree, line, line)
            : new Edit(tree, new TextSpan(span.Start, LineContext.CodeEnd(tree, line) - span.Start), "");
    }

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
    /// Returns the fixes that add a <c>.state</c> after a label that is entered from somewhere
    /// nt65 cannot see. The first gives the processor state the visible paths bring to the line
    /// below the label, or the routine's entry state where the analysis found nothing. The second
    /// gives <c>.state ?</c>, which assumes nothing.
    /// </summary>
    /// <remarks>
    /// Neither fix is preferred. A <c>.state</c> is a contract about every entrant, including the
    /// ones nt65 cannot see, so the inferred one says in its title where its state came from.
    /// </remarks>
    private static IEnumerable<(string Title, IReadOnlyList<Edit> Edits)> StatesAfter(
        ProgramAnalysis analysis, SemanticModel model, Span label)
    {
        var tree = model.Tree;
        var line = label.LineIndex;
        var symbol = DeclaredAt(model, label);
        var block = analysis.FlowFor(tree.Path)?.Regions
            .SelectMany(region => region.Blocks)
            .FirstOrDefault(block => block.Label == symbol && block.On is null);
        var reaching = block is { Steps: [var first, ..] }
            ? analysis.StatesFor(tree.Path)?.AnyBefore(first.Statement)?.Processor
            : null;
        var name = symbol?.DisplayName ?? "the label";
        if ((reaching ?? symbol?.Routine?.Signature?.Entry) is { } state)
        {
            var items = Edits.FormatState(state);
            var source = reaching is not null ? "the state the visible paths bring" : "its routine's entry state";
            yield return ($"Declare `{name}` with {source} (`.state {items}`)", Declared(items));
        }
        yield return ($"Declare `{name}` with `.state ?`", Declared("?"));

        IReadOnlyList<Edit> Declared(string items)
        {
            // A label with a statement after it on its line is split there, because a `.state`
            // declares a label only directly after it.
            var tokens = LineContext.TokensOf(tree, line);
            var colon = tokens.FindIndex(token => token.Kind == SyntaxKind.Colon);
            var body = Edits.BodyIndent(tree, line);
            if (colon >= 0 && colon + 1 < tokens.Count)
            {
                var from = tokens[colon].Start + 1;
                var to = tokens[colon + 1].Start;
                return [new Edit(tree, new TextSpan(from, to - from), $"\n{body}.state {items}\n{body}")];
            }
            return [Edits.InsertAfter(tree, line, $"{body}.state {items}")];
        }
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
        var text = Text(tree, span);
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
        var name = Text(tree, span);
        var tokens = LineContext.TokensOf(tree, diagnostic.Span.LineIndex);
        var colon = tokens.FindIndex(token => token.Start == span.Start) + 1;

        // A member is a name plus what it holds, so it is offered only when a directive follows
        // the label on the line. A bare name can only be a position.
        if (colon > 0 && colon + 1 < tokens.Count && tokens[colon + 1].Kind == SyntaxKind.Directive)
        {
            yield return Fix(diagnostic, $"Make `{name}` a member of the data",
                [new Edit(tree, new TextSpan(span.Start, 0), ".data ")], preferred: false);
        }

        var symbol = DeclaredAt(model, diagnostic.Span);
        var title = $"Make `{name}` a position, `@{name}`";
        yield return symbol is null
            ? Fix(diagnostic, title, [new Edit(tree, new TextSpan(span.Start, 0), "@")], preferred: false)
            : Change.Deferred(title, CodeActionKinds.QuickFix, () => Edits.Rename(program, symbol, "@" + name), diagnostic, false);
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
        var symbol = DeclaredAt(model, diagnostic.Span);

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
