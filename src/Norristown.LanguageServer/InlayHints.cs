using System.Globalization;
using Norristown.Flow;
using Norristown.Layout;
using Norristown.Processor;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// Provides inlay hints, which are the few words drawn in a line to state what the line does not
/// show and without which a reader would misread it. There are five kinds and no more, because
/// the analysis works out far more than anyone wants in front of them, and a hint on every line
/// turns a listing into a dashboard.
/// <para>
/// Three rules shape them. A hint marks a change, not a state, so a width is hinted where it
/// becomes sixteen and not on the forty lines after. A line carries at most one hint at its
/// end, so where two would land the more surprising wins and the other moves into its tooltip.
/// And every hint explains itself in a sentence in its tooltip, naming the declaration that
/// decided it where there is one, because the hint itself is too short to say so.
/// </para>
/// <para>
/// Hints are computed only for the lines the editor asks about, because clients fetch them as a
/// file is scrolled, and a keystroke should not pay for lines nobody is looking at.
/// </para>
/// </summary>
internal static class InlayHints
{
    /// <summary>
    /// The maximum length of a hint. Beyond about this length a hint stops reading as a note in
    /// the margin and starts pushing the line it is about off the screen. Whatever does not fit is
    /// in the tooltip.
    /// </summary>
    private const int MaximumCharacters = 12;

    /// <summary>
    /// Returns the hints for the lines <paramref name="first"/> to <paramref name="last"/>,
    /// inclusive.
    /// </summary>
    /// <param name="analysis">The analysis of the whole program.</param>
    /// <param name="model">The file being hinted.</param>
    /// <param name="settings">Which kinds the editor shows.</param>
    /// <param name="first">The first line the editor is showing.</param>
    /// <param name="last">The last line the editor is showing.</param>
    /// <param name="cancellation">
    /// The token checked between lines, since a range may be a screenful or a whole file.
    /// </param>
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

        // The state after a line is the state before whatever runs next, and only the order in
        // which layout walked the code says which statement that is.
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
    /// Returns every hint that could go at the end of one line, most surprising first. Only the
    /// first is shown and the rest go in its tooltip, because with two notes at the end of one line
    /// neither gets read.
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
    /// Builds the single hint shown at a line's end, with the other hints that would have gone
    /// there appended to its tooltip.
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

    /// <summary>
    /// Cuts a label to what fits, ending it with an ellipsis where it did not fit. The tooltip
    /// has the whole label.
    /// </summary>
    private static string Shortened(string label) =>
        label.Length <= MaximumCharacters ? label : label[..(MaximumCharacters - 1)].TrimEnd() + "…";

    /// <summary>
    /// Maps each statement to what runs after it. That is the next step of its basic block, or,
    /// for the statement that ends a block, the first step of the block that control falls
    /// through into. A call ends a block and returns into the next one, and a branch not taken
    /// falls through into it. A statement that control never falls through — a return or a jump —
    /// has no entry, because the code below it is reached by some other path, not by running on
    /// from this line.
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
    /// Returns a hint listing the register widths, emulation flag, D or B that the line changes,
    /// with only the parts that differ. An <c>.ensure</c> or a <c>.state</c> already states its
    /// effect, so neither is hinted.
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
            parts.Add(ProcessorState.Format("a", now.A));
        if (now.Index != was.Index)
            parts.Add(ProcessorState.Format("i", now.Index));
        if (now.E != was.E)
            parts.Add(ProcessorState.Format(now.E));
        if (now.D != was.D)
            parts.Add(now.D.Format("dp"));
        if (now.B != was.B)
            parts.Add(now.B.Format("dbr"));
        if (parts.Count == 0)
            return null;

        // A call that changes the state is the surprising case: the change is made in the
        // called routine rather than on this line, and the arrow marks that it came from there.
        var calls = Instruction(statement) is { } instruction
            && Instructions.Facts(instruction.MnemonicKind).Control == Control.Calls;
        return new Mark(
            (calls ? "→ " : "") + string.Join(" ", parts),
            $"What reaches the next line is `{now}`, and what reached this one was `{was}`.");
    }

    /// <summary>
    /// Returns a hint for a conditional branch whose target is out of reach of the two-byte form,
    /// so that layout emitted it as the opposite branch around a <c>jmp</c>.
    /// </summary>
    private static Mark? Lengthened(InstructionStatementSyntax branch, LineLayout laid)
    {
        if (!SyntaxFacts.IsLongBranch(branch.MnemonicKind))
            return null;
        var mnemonic = SyntaxFacts.TextOf(branch.MnemonicKind);
        var over = SyntaxFacts.TextOf(Instructions.FormsOf(branch.MnemonicKind).Skipped);
        var cost = laid.Cycles is { } cycles ? $" and {Lsp.Format(cycles)}" : "";
        return new Mark(
            "long",
            $"`{mnemonic}` cannot reach its target in the two-byte form, so it is written as a "
                + $"`{over}` over a `jmp`: {laid.Length} bytes{cost}.");
    }

    /// <summary>
    /// Returns a hint for a value a declaration does not give explicitly. Such a value is the
    /// value an enum member takes from the one before it, a struct or union member's offset, or
    /// the value a constant defined by an expression works out to.
    /// </summary>
    private static Mark? Implied(SemanticModel model, StatementSyntax statement)
    {
        // A struct or union member is a labelled line inside the type's body, and its offset is
        // what a reader of the body wants to know.
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
        if (statement is EnumMemberSyntax { Value: null } declaration
            && model.SymbolAt(declaration.Name) is { Value.IsKnown: true } named)
        {
            return new Mark(
                $"= {named.Value}",
                named.PreviousMember is { } before
                    ? $"`{named.Name}` is given no value, so it is `{before.Name}` and one more."
                    : $"`{named.Name}` is given no value, and is the first member, so it is zero.",
                Protocol.InlayHintKind.Type);
        }

        // A constant defined by a literal already shows its value. One defined by an expression
        // over other names does not, and the resulting value is what the expression computes.
        if (statement is ConstantDeclarationSyntax { Value: not LiteralExpressionSyntax } constant
            && model.SymbolAt(constant.Name) is { Value.IsKnown: true } value)
        {
            return new Mark(
                $"= {value.Value}",
                $"`{value.Name}` works out to {Format(value.Value)}.",
                Protocol.InlayHintKind.Type);
        }
        return null;
    }

    /// <summary>
    /// Returns a hint giving a line's cycle count. For an instruction, it is the instruction's own
    /// count. For a label on a line of its own, it is the count of the basic block the label
    /// starts, which is the run of lines under it that always execute together.
    /// </summary>
    private static Mark? Counted(
        SemanticModel model, ControlFlow? flow, LineLayout? laid, StatementSyntax statement)
    {
        if (laid is { Cycles: { } cycles } && Instruction(statement) is not null)
        {
            var why = laid.Causes is { Count: > 0 } causes && !cycles.IsExact
                ? " " + string.Join(", ", causes) + "."
                : "";
            return new Mark(cycles.ToString(), $"This line takes {Lsp.Format(cycles)}.{why}");
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
                + $"label or branch, take {Lsp.Format(total)} together.");
    }

    /// <summary>
    /// Adds hints naming the parameter each positional argument of a call is for. No hint is given
    /// for an argument that is itself the parameter's name, for a macro call that uses named
    /// arguments, or for a callee with fewer than two parameters, where there is nothing to wonder
    /// about.
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

    /// <summary>
    /// Returns the arguments of a macro call that get a parameter-name hint, each with its
    /// parameter's name.
    /// </summary>
    private static IReadOnlyList<(string Parameter, SyntaxNode Argument)> Given(
        SemanticModel model, MacroCallSyntax call)
    {
        if (call.Arguments is not { } arguments || model.MacroAt(call) is not { } macro
            || macro.Parameters.Count(parameter => !parameter.IsBlock) < 2
            || arguments.Arguments.Any(argument => argument is NamedArgumentSyntax))
        {
            return [];
        }

        // Let the binder match arguments to parameters: a `list` parameter takes all remaining
        // arguments, and an omitted parameter takes its default.
        var invocation = MacroInvocation.Of(call, macro, model.Tree, null);
        return
        [
            .. invocation.Arguments
                .Where(given => given is { IsGiven: true, Value: not null } && !given.Parameter.IsBlock
                    && !Matches(given.Value, given.Parameter.Name))
                .Select(given => (given.Parameter.Name, given.Value!)),
        ];
    }

    /// <summary>
    /// Returns the arguments of a call to a <c>.func</c> that get a parameter-name hint, each
    /// with its parameter's name. A <c>.func</c> call's arguments are always positional.
    /// </summary>
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

    /// <summary>
    /// Checks whether an argument is just the name of its parameter, so that a hint would only
    /// repeat it.
    /// </summary>
    private static bool Matches(SyntaxNode argument, string parameter) =>
        argument is NameExpressionSyntax { SimpleName: { } word } && word.Text == parameter;

    /// <summary>
    /// Returns the instruction a statement holds, including one after a label, or null for a
    /// statement that holds none.
    /// </summary>
    private static InstructionStatementSyntax? Instruction(StatementSyntax? statement) => statement switch
    {
        InstructionStatementSyntax instruction => instruction,
        LabeledLineSyntax labelled => labelled.Statement as InstructionStatementSyntax,
        _ => null,
    };

    /// <summary>
    /// Formats a value as a tooltip sentence shows it, in nt65's own form, followed by its
    /// decimal value from 10 upward, where the two differ.
    /// </summary>
    private static string Format(Value value) => value.AsNumber() is { } number && number >= 10
        ? $"`{value}`, which is {number.ToString(CultureInfo.InvariantCulture)}"
        : $"`{value}`";

    private static string Plural(long count, string what) =>
        $"{count.ToString(CultureInfo.InvariantCulture)} {what}{(count == 1 ? "" : "s")}";

    /// <summary>
    /// Represents one hint that could go at the end of a line, with its label, its meaning, and
    /// which of the protocol's two hint kinds it is, if either.
    /// </summary>
    /// <param name="Label">The few characters the editor draws.</param>
    /// <param name="Tooltip">What the label means, in a sentence.</param>
    /// <param name="Kind">
    /// The protocol's kind for the hint, or null where the protocol has no name for it.
    /// </param>
    private sealed record Mark(string Label, string Tooltip, Protocol.InlayHintKind? Kind = null);
}
