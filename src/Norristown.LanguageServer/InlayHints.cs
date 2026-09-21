using System.Globalization;
using Norristown.Flow;
using Norristown.Layout;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// The few words drawn in a line that the line does not say and that a reader would read the
/// line wrong without. There are five kinds and no more: the analysis works out far more than
/// anyone wants in front of them, and a hint on every line is a dashboard rather than a
/// listing.
/// <para>
/// Three rules hold the shape. A hint marks a change and not a state, so a width is hinted
/// where it becomes sixteen and not on the forty lines after. A line carries at most one hint
/// at its end, so where two would land the more surprising wins and the other moves into its
/// tooltip. And every hint says what it means in a sentence, with the declaration that decided
/// it where there is one, because the hint itself is too short to.
/// </para>
/// <para>
/// Only the lines the editor asks about are worked out: hints are fetched as a file is
/// scrolled, and a keystroke must not pay for the lines nobody is looking at.
/// </para>
/// </summary>
internal static class InlayHints
{
    /// <summary>
    /// How long a hint may be. Past about this it stops reading as a note in the margin and
    /// starts pushing the line it is about off the screen; what will not fit is in the tooltip.
    /// </summary>
    private const int MostCharacters = 12;

    /// <summary>
    /// The hints for the lines <paramref name="first"/> to <paramref name="last"/>, inclusive.
    /// </summary>
    /// <param name="analysis">Everything the program means.</param>
    /// <param name="model">The file being hinted.</param>
    /// <param name="settings">Which kinds the editor shows.</param>
    /// <param name="first">The first line the editor is showing.</param>
    /// <param name="last">The last.</param>
    /// <param name="cancellation">Asked between lines, since a range may be a screenful or a file.</param>
    public static IReadOnlyList<Protocol.InlayHint> In(
        ProgramAnalysis analysis, SemanticModel model, HintSettings settings,
        int first, int last, CancellationToken cancellation)
    {
        var hints = new List<Protocol.InlayHint>();
        if (!settings.Any)
            return hints;
        var tree = model.Tree;
        var layout = analysis.LayoutFor(tree.Path);
        var flow = analysis.FlowFor(tree.Path);
        var states = analysis.StatesFor(tree.Path);

        // What the state after a line is is the state before whatever runs next, and only the
        // order layout walked the bytes in says which statement that is.
        var following = settings.StateChanges && states is not null ? Following(flow) : [];
        for (var i = Math.Max(first, 0); i <= last && i < tree.LineCount; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            var line = tree.GetLine(i);
            if (settings.ParameterNames)
                Arguments(model, line, hints);
            var marks = Ending(analysis, model, settings, layout, flow, states, following, line).ToList();
            if (marks.Count > 0)
                hints.Add(Ended(tree, i, marks));
        }
        return hints;
    }

    /// <summary>
    /// Everything that would stand at the end of one line, most surprising first. Only the
    /// first of them is shown; the rest are in its tooltip, because a line is one place and
    /// two notes in it are neither of them read.
    /// </summary>
    private static IEnumerable<Mark> Ending(
        ProgramAnalysis analysis, SemanticModel model, HintSettings settings, CodeLayout? layout,
        ControlFlow? flow, StateAnalysis? states, IReadOnlyDictionary<int, SyntaxNode> following,
        LineSyntax line)
    {
        var statement = line.Statement;
        var laid = layout?.AnyOf(statement);
        if (settings.StateChanges && states is not null && Changed(states, following, statement) is { } changed)
            yield return changed;
        if (settings.LongBranches && laid is { Inverted: true }
            && Instruction(statement) is { } branch && Lengthened(branch, laid) is { } lengthened)
        {
            yield return lengthened;
        }
        if (settings.ImpliedValues && Implied(model, statement) is { } implied)
            yield return implied;
        if (settings.Cycles && Counted(model, flow, laid, statement) is { } counted)
            yield return counted;
    }

    /// <summary>
    /// The one hint a line's end carries, with whatever else would have stood there written
    /// under its sentence.
    /// </summary>
    private static Protocol.InlayHint Ended(SyntaxTree tree, int line, IReadOnlyList<Mark> marks)
    {
        var tooltip = string.Join("\n\n", marks.Select(mark =>
            mark == marks[0] ? mark.Tooltip : $"**{mark.Label}** — {mark.Tooltip}"));
        return new Protocol.InlayHint(
            Lsp.ToPosition(tree, LineContext.CodeEnd(tree, line)),
            Shortened(marks[0].Label),
            marks[0].Kind,
            Protocol.MarkupContent.Markdown(tooltip),
            PaddingLeft: true);
    }

    /// <summary>A label, cut to what fits with an ellipsis where it did not; the tooltip has it whole.</summary>
    private static string Shortened(string label) =>
        label.Length <= MostCharacters ? label : label[..(MostCharacters - 1)].TrimEnd() + "…";

    /// <summary>
    /// What runs after each statement: the next step of its block, and for the statement that
    /// ends one, the first step of the block control carries on into. A call ends a block and
    /// comes back into the next, and a branch not taken carries on into it. A statement nothing
    /// falls out of — a return, a jump — has none, because what is written under it is reached
    /// by a path rather than by running off the end of this line.
    /// </summary>
    private static Dictionary<int, SyntaxNode> Following(ControlFlow? flow)
    {
        var following = new Dictionary<int, SyntaxNode>();
        foreach (var region in flow?.Regions ?? [])
        {
            var blocks = region.Blocks;
            for (var b = 0; b < blocks.Count; b++)
            {
                var steps = blocks[b].Steps;
                for (var i = 0; i + 1 < steps.Count; i++)
                    following.TryAdd(steps[i].Statement.Position, steps[i + 1].Statement);
                if (steps.Count > 0 && b + 1 < blocks.Count && blocks[b + 1] is { IsFallenInto: true } after
                    && after.Steps.Count > 0)
                {
                    following.TryAdd(steps[^1].Statement.Position, after.Steps[0].Statement);
                }
            }
        }
        return following;
    }

    /// <summary>
    /// A width, the emulation flag, D or B that the line leaves different from how it found
    /// them, and only the parts that differ. An <c>.ensure</c> and a <c>.state</c> say it
    /// themselves, so neither is hinted.
    /// </summary>
    private static Mark? Changed(
        StateAnalysis states, IReadOnlyDictionary<int, SyntaxNode> following, StatementSyntax statement)
    {
        if (statement is EnsureDirectiveSyntax or StateDirectiveSyntax
            || !following.TryGetValue(statement.Position, out var after)
            || states.AnyBefore(statement)?.Processor is not { } was
            || states.AnyBefore(after)?.Processor is not { } now)
        {
            return null;
        }
        var parts = new List<string>();
        if (now.A != was.A)
            parts.Add(ProcessorState.Spell("a", now.A));
        if (now.Index != was.Index)
            parts.Add(ProcessorState.Spell("i", now.Index));
        if (now.E != was.E)
            parts.Add(ProcessorState.Spell(now.E));
        if (now.D != was.D)
            parts.Add(now.D.Spell("dp"));
        if (now.B != was.B)
            parts.Add(now.B.Spell("dbr"));
        if (parts.Count == 0)
            return null;

        // A call that changes the state is the surprising one: the change is written in the
        // routine it calls rather than in this line, and the arrow says it came from there.
        var calls = Instruction(statement) is { } instruction
            && Instructions.Facts(instruction.Mnemonic.Text.ToLowerInvariant()).Calls;
        return new Mark(
            (calls ? "→ " : "") + string.Join(" ", parts),
            $"What reaches the next line is `{now}`, and what reached this one was `{was}`.");
    }

    /// <summary>
    /// A conditional branch whose target is out of reach of the two-byte form, which layout
    /// wrote as the opposite branch over a <c>jmp</c>.
    /// </summary>
    private static Mark? Lengthened(InstructionStatementSyntax branch, LineLayout laid)
    {
        var mnemonic = branch.Mnemonic.Text.ToLowerInvariant();
        if (!SyntaxFacts.LongBranches.Contains(mnemonic))
            return null;
        var over = Instructions.FormsOf(mnemonic).Skipped;
        var cost = laid.Cycles is { } cycles ? $" and {Lsp.Spell(cycles)}" : "";
        return new Mark(
            "long",
            $"`{mnemonic}` cannot reach its target in the two-byte form, so it is written as a "
                + $"`{over}` over a `jmp`: {laid.Length} bytes{cost}.");
    }

    /// <summary>
    /// A value a declaration does not write: the value an enum member takes from the one before
    /// it, where a struct or union member lands, and what a constant written as a sum works out
    /// to.
    /// </summary>
    private static Mark? Implied(SemanticModel model, StatementSyntax statement)
    {
        // A member of a layout is a labelled line inside a type's body, and where it lands is
        // the fact the body is read for.
        if (statement is LabeledLineSyntax labelled
            && model.SymbolAt(labelled.Label.Name) is { Kind: SymbolKind.Member } member)
        {
            return member.Value.AsNumber() is not { } offset
                ? null
                : new Mark(
                    $"+{member.Value}",
                    $"`{member.DisplayName}` starts {Plural(offset, "byte")} into "
                        + $"`{member.Scope.Owner?.DisplayName ?? "the layout"}`.",
                    Protocol.InlayHintKind.Type);
        }
        if (statement is EnumMemberSyntax { Value: null } written
            && model.SymbolAt(written.Name) is { Value.IsKnown: true } named)
        {
            return new Mark(
                $"= {named.Value}",
                named.PreviousMember is { } before
                    ? $"`{named.Name}` is given no value, so it is `{before.Name}` and one more."
                    : $"`{named.Name}` is given no value, and is the first member, so it is zero.",
                Protocol.InlayHintKind.Type);
        }

        // A constant written as a literal already says what it is worth; one written as a sum
        // of other names does not, and what it comes to is why it was written that way.
        if (statement is ConstantDeclarationSyntax { Value: not LiteralExpressionSyntax } constant
            && model.SymbolAt(constant.Name) is { Value.IsKnown: true } value)
        {
            return new Mark(
                $"= {value.Value}",
                $"`{value.Name}` works out to {Spell(value.Value)}.",
                Protocol.InlayHintKind.Type);
        }
        return null;
    }

    /// <summary>
    /// What a line costs: an instruction's own count, and a label's the count of the block it
    /// opens, which is the run of lines under it that always run together.
    /// </summary>
    private static Mark? Counted(
        SemanticModel model, ControlFlow? flow, LineLayout? laid, StatementSyntax statement)
    {
        if (laid is { Cycles: { } cycles } && Instruction(statement) is not null)
        {
            var why = laid.Causes is { Count: > 0 } causes && !cycles.IsExact
                ? " " + string.Join(", ", causes) + "."
                : "";
            return new Mark(cycles.ToString(), $"This line takes {Lsp.Spell(cycles)}.{why}");
        }
        if (statement is not LabeledLineSyntax { Statement: null } labelled
            || model.SymbolAt(labelled.Label.Name) is not { } label)
        {
            return null;
        }
        var block = flow?.Regions
            .SelectMany(region => region.Blocks)
            .FirstOrDefault(block => block.Label == label && block.On is null);
        return block?.Cycles is not { } total
            ? null
            : new Mark($"block {total}", $"The lines under `{label.DisplayName}`, as far as the next "
                + $"label or branch, take {Lsp.Spell(total)} together.");
    }

    /// <summary>
    /// Which parameter each positional argument of a call is for. An argument that names the
    /// parameter it is for already says it, and so does one written as a named argument; a call
    /// that takes one argument leaves nothing to wonder about.
    /// </summary>
    private static void Arguments(SemanticModel model, LineSyntax line, List<Protocol.InlayHint> hints)
    {
        foreach (var node in line.DescendantNodes())
        {
            var named = node switch
            {
                MacroCallSyntax call => Given(model, call),
                CallExpressionSyntax { Callee: not null } call => Given(model, call),
                _ => [],
            };
            foreach (var (parameter, argument) in named)
            {
                hints.Add(new Protocol.InlayHint(
                    Lsp.ToPosition(model.Tree, argument.Span.Start),
                    parameter + ":",
                    Protocol.InlayHintKind.Parameter,
                    Protocol.MarkupContent.Markdown($"The argument for `{parameter}`."),
                    PaddingRight: true));
            }
        }
    }

    /// <summary>The arguments of a macro call worth naming the parameter of, with that name.</summary>
    private static IReadOnlyList<(string Parameter, SyntaxNode Argument)> Given(
        SemanticModel model, MacroCallSyntax call)
    {
        if (call.Arguments is not { } written || model.MacroAt(call) is not { } macro
            || macro.Parameters.Count(parameter => !parameter.IsBlock) < 2
            || written.Arguments.Any(argument => argument is NamedArgumentSyntax))
        {
            return [];
        }

        // Which argument went to which parameter is the binder's to say: a `list` parameter
        // takes every argument left, and one left out takes its default.
        var invocation = MacroInvocation.Of(call, macro, model.Tree, null);
        return
        [
            .. invocation.Arguments
                .Where(given => given is { Written: true, Value: not null } && !given.Parameter.IsBlock
                    && !Matches(given.Value, given.Parameter.Name))
                .Select(given => (given.Parameter.Name, given.Value!)),
        ];
    }

    /// <summary>The same for a call to a <c>.func</c>, whose arguments are positional and nothing else.</summary>
    private static IReadOnlyList<(string Parameter, SyntaxNode Argument)> Given(
        SemanticModel model, CallExpressionSyntax call)
    {
        if (model.SymbolOf(call.Callee!) is not { Kind: SymbolKind.Func } function
            || function.ParameterSymbols.Count < 2)
        {
            return [];
        }
        var given = new List<(string, SyntaxNode)>();
        for (var i = 0; i < call.Arguments.Arguments.Count && i < function.ParameterSymbols.Count; i++)
        {
            var argument = call.Arguments.Arguments[i];
            var name = function.ParameterSymbols[i].Name;
            if (!Matches(argument, name))
                given.Add((name, argument));
        }
        return given;
    }

    /// <summary>Whether an argument is written as the name of the parameter it is for, which says it already.</summary>
    private static bool Matches(SyntaxNode argument, string parameter) =>
        argument is NameExpressionSyntax { SimpleName: { } word } && word.Text == parameter;

    /// <summary>The instruction a line holds, a labelled one included; null for a line holding none.</summary>
    private static InstructionStatementSyntax? Instruction(StatementSyntax? statement) => statement switch
    {
        InstructionStatementSyntax instruction => instruction,
        LabeledLineSyntax labelled => labelled.Statement as InstructionStatementSyntax,
        _ => null,
    };

    /// <summary>A value as a sentence reads it: nt65's own spelling, with the decimal where it differs.</summary>
    private static string Spell(Value value) => value.AsNumber() is { } number && number >= 10
        ? $"`{value}`, which is {number.ToString(CultureInfo.InvariantCulture)}"
        : $"`{value}`";

    private static string Plural(long count, string what) =>
        $"{count.ToString(CultureInfo.InvariantCulture)} {what}{(count == 1 ? "" : "s")}";

    /// <summary>
    /// One thing that could stand at the end of a line: what it says, what that means, and
    /// which of the protocol's two kinds it is, where it is one of them.
    /// </summary>
    /// <param name="Label">The few characters the editor draws.</param>
    /// <param name="Tooltip">What they mean, in a sentence.</param>
    /// <param name="Kind">What it is, or null where the protocol has no name for it.</param>
    private sealed record Mark(string Label, string Tooltip, Protocol.InlayHintKind? Kind = null);
}
