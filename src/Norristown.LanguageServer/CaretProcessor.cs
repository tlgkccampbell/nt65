using System.Globalization;
using Norristown.Flow;
using Norristown.Processor;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// Answers the <c>nt65/processor</c> request. It gives, for the line at the caret, the rows the
/// instruction hover shows below its rule, as data for a view that follows the caret. Those are the
/// widths and the mode, D and B, what each register holds, the flags, and the stack.
/// <para>
/// A row whose fact is not known says so rather than being left out, so that the view keeps its
/// shape from one line to the next. Where it is known, a register row also says which line set
/// its value and what constant it holds.
/// </para>
/// <para>
/// The stack is known relative to the routine's entry only, so by default its rows end at the
/// stack the routine was entered with. A reader may choose one call to the routine, and the rows
/// then go on through the return address into the caller's own pushes. That is true only along the
/// path through that call, which is why it is a choice and never the default.
/// </para>
/// </summary>
internal static class CaretProcessor
{
    /// <summary>The flags the view lists, in the order the status register holds them.</summary>
    private static readonly (StatusFlags Flag, string Name)[] Flags =
    [
        (StatusFlags.Negative, "N"), (StatusFlags.Overflow, "V"), (StatusFlags.Decimal, "D"),
        (StatusFlags.InterruptDisable, "I"), (StatusFlags.Zero, "Z"), (StatusFlags.Carry, "C"),
    ];

    /// <summary>
    /// Returns what is known of the processor at <paramref name="position"/> in
    /// <paramref name="model"/>'s file, or null where the caret is in no routine the analysis
    /// followed.
    /// </summary>
    /// <param name="analysis">The program.</param>
    /// <param name="model">The caret's file.</param>
    /// <param name="position">The caret.</param>
    /// <param name="chosen">
    /// The calls the reader chose to see routines entered from. The one that calls this routine, if
    /// any does, is used.
    /// </param>
    /// <param name="uriOf">Returns the URI the client knows a file's path by.</param>
    public static Protocol.ProcessorResult? At(
        ProgramAnalysis analysis, SemanticModel model, int position, IReadOnlyList<Protocol.Location> chosen, Func<string, string> uriOf)
    {
        var tree = model.Tree;
        if (analysis.FileFor(tree.Path) is not { } file || Routine(tree, file.Flow, position) is not var (region, extent)
            || Statement(analysis, file, tree, position, extent) is not { } statement)
        {
            return null;
        }
        var flow = file.Flow;
        var state = file.State?.AnyBefore(statement);
        var registers = flow.Registers?.AnyBefore(statement);
        var known = flow.KnownBefore(statement);
        var uri = uriOf(tree.Path);
        var rows = new List<Protocol.ProcessorRow>();

        // Only the 65816 has widths, a mode, a direct page register and a data bank.
        if (analysis.Cpu == Cpu.Wdc65816)
        {
            // Where the expansions of a macro body differ, the row shows what they agree on, and
            // the rows under it list each state with how many expansions it reaches, as the hover
            // does.
            var processor = state?.Processor ?? ProcessorState.Unknown;
            var each = file.State is { } states ? Hovers.ByExpansion(states.EachBefore(statement), Widths) : [];
            rows.Add(each.Count == 0
                ? Row("state", Widths(processor))
                : new Protocol.ProcessorRow("state", Widths(processor), $"{each.Sum(one => one.Count)} expansions differ", null,
                    [.. each.Select(one => Row($"×{one.Count}", one.State))]));
            rows.Add(Row("D", Describe(processor.D, 4)));
            rows.Add(Row("B", Describe(processor.B, 2)));
        }

        var sources = InputSources.Held(analysis, model, statement.Span.Start)?.Inputs
            .ToDictionary(input => input.Name, StringComparer.Ordinal);
        foreach (var register in RegisterEffects.Each(Registers.A | Registers.X | Registers.Y))
        {
            var name = RegisterEffects.Format(register);
            var bits = register == Registers.A ? state?.Processor.A : state?.Processor.Index;
            var value = Constant(known, register) is { } constant
                ? Hex(constant, bits)
                : registers is { } held ? Hovers.Held(held.Of(register), register) : "unknown";
            var source = sources?.GetValueOrDefault(name);
            rows.Add(new Protocol.ProcessorRow(name, value, Detail(tree, source), Target(tree, uri, source), null));
        }
        rows.Add(Row("flags", FlagsOf(known), Entered(registers)));

        var stack = StackRows.Of(analysis, registers, state);
        var callers = Callers(analysis, region.Routine, uriOf);
        (Protocol.ProcessorCaller Caller, InstructionStatementSyntax Call)? through =
            callers.FirstOrDefault(each => chosen.Any(location => Same(each.Caller.At, location)));
        if (through is { Caller: null })
            through = null;
        rows.Add(Stack(analysis, stack, through));
        return new Protocol.ProcessorResult(
            region.Routine.DisplayName,
            tree.GetLineIndex(statement.Span.Start),
            rows,
            [.. callers.Select(each => each.Caller)],
            through?.Caller.At);
    }

    /// <summary>
    /// Returns the innermost routine of <paramref name="flow"/> whose lines hold
    /// <paramref name="position"/>, with the span of its lines. A routine declared with a block
    /// spans the block. Any other spans from the line that opens it to the last of its statements
    /// in the file.
    /// </summary>
    private static (FlowRegion Region, TextSpan Extent)? Routine(SyntaxTree tree, ControlFlow flow, int position)
    {
        (FlowRegion Region, TextSpan Extent)? found = null;
        foreach (var region in flow.Regions)
        {
            if (region.Routine.Tree != tree)
                continue;
            var opener = tree.GetLine(tree.GetLineIndex(region.Routine.NameSpan.Start));
            var extent = opener.Parent is BlockSyntax block && block.Opener == opener ? block.Span : Spanned(tree, region, opener);
            if (position < extent.Start || position > extent.End)
                continue;
            if (found is null || extent.Length < found.Value.Extent.Length)
                found = (region, extent);
        }
        return found;
    }

    /// <summary>Returns the span from <paramref name="opener"/> to the last of the routine's statements in the file.</summary>
    private static TextSpan Spanned(SyntaxTree tree, FlowRegion region, SyntaxNode opener)
    {
        var end = region.Blocks
            .SelectMany(block => block.Steps)
            .Where(step => step.Statement.Tree == tree)
            .Select(step => step.Statement.Span.End)
            .Append(opener.Span.End)
            .Max();
        return new TextSpan(opener.Span.Start, end - opener.Span.Start);
    }

    /// <summary>
    /// Returns the statement the rows are for, which is the one on the caret's line, or where that
    /// line holds no statement the analysis reached, the first after it in the routine that does.
    /// So the line that opens a routine shows what the routine is entered with.
    /// </summary>
    private static StatementSyntax? Statement(
        ProgramAnalysis analysis, FileAnalysis file, SyntaxTree tree, int position, TextSpan extent)
    {
        var last = tree.GetLineIndex(extent.End);
        for (var line = tree.GetLineIndex(position); line <= last; line++)
        {
            if (tree.GetLine(line).Statement is not { } statement)
                continue;
            if (file.Flow.Registers?.AnyBefore(statement) is not null || file.Flow.KnownBefore(statement) is not null
                || (analysis.Cpu == Cpu.Wdc65816 && file.State?.AnyBefore(statement) is not null))
            {
                return statement;
            }
        }
        return null;
    }

    /// <summary>Returns the constant <paramref name="register"/> holds, or null where it is not known.</summary>
    private static long? Constant(KnownValues? known, Registers register) => register switch
    {
        Registers.A => known?.A,
        Registers.X => known?.X,
        _ => known?.Y,
    };

    /// <summary>
    /// Formats a constant at the register's width, with the decimal beside it from ten upward as
    /// the hover shows a value. A register of unknown width is shown as wide as the value needs.
    /// </summary>
    private static string Hex(long value, Width? width)
    {
        var digits = width == Width.Sixteen || (width != Width.Eight && value > 0xff) ? 4 : 2;
        var hex = StateValue.Hex(value, digits);
        return value >= 10 ? $"{hex} ({value.ToString(CultureInfo.InvariantCulture)})" : hex;
    }

    /// <summary>
    /// Describes D or B, which is a value, a set of banks, the value the routine was entered with,
    /// or not known.
    /// </summary>
    private static string Describe(StateValue value, int digits) => value.Kind switch
    {
        StateValueKind.Known or StateValueKind.Among or StateValueKind.Within => value.Describe(digits),
        StateValueKind.Unchanged => "as entered",
        _ => "unknown",
    };

    /// <summary>
    /// Returns where a register's value was set, such as <c>set at line 5</c>, or null where it
    /// holds only what the routine was entered with.
    /// </summary>
    private static string? Detail(SyntaxTree tree, SourcedInput? input)
    {
        if (input is null)
            return null;
        var set = input.Sources
            .Where(source => source.Kind is not (SourceKind.Entry or SourceKind.Unknown))
            .Select(source => tree.GetLineIndex(source.Line.Start) + 1)
            .Distinct()
            .ToList();
        var lost = input.Sources.FirstOrDefault(source => source.Kind == SourceKind.Unknown);
        var words = new List<string>();
        if (set.Count > 0)
            words.Add($"set at line{(set.Count == 1 ? "" : "s")} {string.Join(", ", set)}");
        if (lost?.Reason is { } reason)
            words.Add(reason);
        return words.Count == 0 ? null : string.Join("; ", words);
    }

    /// <summary>Returns the first line that set a register's value, for the row to lead to.</summary>
    private static Protocol.Location? Target(SyntaxTree tree, string uri, SourcedInput? input) =>
        input?.Sources.FirstOrDefault(source => source.Kind != SourceKind.Entry) is { } source
            ? new Protocol.Location(uri, Lsp.ToRange(tree, source.Line))
            : null;

    /// <summary>
    /// Formats the flags in the order the status register holds them, each as <c>0</c>, <c>1</c>
    /// or <c>?</c>, such as <c>N 0  V ?  D 0  I ?  Z 0  C ?</c>.
    /// </summary>
    private static string FlagsOf(KnownValues? known)
    {
        var flags = known?.Flags ?? FlagValues.None;
        return string.Join("  ", Flags.Select(flag =>
            $"{flag.Name} {((flags.Known & flag.Flag) == 0 ? "?" : (flags.Set & flag.Flag) != 0 ? "1" : "0")}"));
    }

    /// <summary>
    /// Returns which flags still hold what the routine was entered with, such as
    /// <c>C, V as entered</c>, or null where none does.
    /// </summary>
    private static string? Entered(RegisterState? registers)
    {
        if (registers is null)
            return null;
        var kept = RegisterEffects.Each(Registers.Flags).Where(flag => registers.Of(flag).Holds(flag)).ToList();
        return kept.Count == 0 ? null : string.Join(", ", kept.Select(RegisterEffects.Format)) + " as entered";
    }

    /// <summary>
    /// Returns the stack row, whose rows are the routine's pushes, top first. Below them is the stack
    /// the routine was entered with or, through the <paramref name="chosen"/> call, the return
    /// address and then the caller's pushes.
    /// </summary>
    private static Protocol.ProcessorRow Stack(
        ProgramAnalysis analysis,
        IReadOnlyList<(string Text, int? Bytes)>? pushes,
        (Protocol.ProcessorCaller Caller, InstructionStatementSyntax Call)? chosen)
    {
        if (pushes is null)
            return Row("stack", "unknown");
        var rows = new List<Protocol.ProcessorRow>();
        int? depth = 0;
        foreach (var (text, bytes) in pushes)
            rows.Add(Pushed(analysis, text, bytes, ref depth));
        var pushed = pushes.Count == 0 ? "nothing pushed"
            : pushes.All(push => push.Bytes is not null) ? Bytes(pushes.Sum(push => push.Bytes!.Value)) + " pushed"
            : $"{pushes.Count} push{(pushes.Count == 1 ? "" : "es")}";

        if (chosen is not var (caller, call))
        {
            rows.Add(Row("entry", "the stack the routine was entered with"));
            return new Protocol.ProcessorRow("stack", pushed, "top first", null, rows);
        }

        // The return address is what the call itself pushed, and below it is whatever the caller
        // had pushed by then.
        rows.Add(Pushed(analysis, "return address", ReturnBytes(call), ref depth) with { Target = caller.At });
        var file = analysis.FileFor(call.Tree.Path);
        var below = StackRows.Of(analysis, file?.Flow.Registers?.AnyBefore(call), file?.State?.AnyBefore(call));
        if (below is null)
        {
            rows.Add(Row("", "the rest of the caller's stack is unknown"));
        }
        else
        {
            foreach (var (text, bytes) in below)
            {
                var row = Pushed(analysis, text, bytes, ref depth);
                rows.Add(row with { Detail = row.Detail is { } size ? $"{size}, pushed by {caller.Name}" : $"pushed by {caller.Name}" });
            }
            rows.Add(Row("entry", $"the stack {caller.Name} was entered with"));
        }
        return new Protocol.ProcessorRow("stack", pushed, $"top first, as called from {caller.Name}", null, rows);
    }

    /// <summary>
    /// Returns the row for one push, keyed on the 65816 by the stack-relative offset that reads its
    /// lowest byte, such as <c>3,s</c>. The offsets below a push of unknown size are not known.
    /// </summary>
    private static Protocol.ProcessorRow Pushed(ProgramAnalysis analysis, string text, int? bytes, ref int? depth)
    {
        string key;
        if (analysis.Cpu != Cpu.Wdc65816)
            key = depth == 0 ? "top" : "";
        else
            key = depth is { } above ? $"{above + 1},s" : "?,s";
        depth = depth is { } known && bytes is { } more ? known + more : null;
        return new Protocol.ProcessorRow(key, text, bytes is { } size ? Bytes(size) : null, null, null);
    }

    /// <summary>Formats a count of bytes, such as <c>1 byte</c> or <c>3 bytes</c>.</summary>
    private static string Bytes(int count) => count == 1 ? "1 byte" : $"{count} bytes";

    /// <summary>
    /// Returns each call to <paramref name="routine"/> that the reader may choose to see it entered
    /// from, in the order of the files and then of the lines. That is a <c>jsr</c> or a
    /// <c>jsl</c> written in a file, since a call a macro expands has as many stacks as the macro
    /// has calls.
    /// </summary>
    private static IReadOnlyList<(Protocol.ProcessorCaller Caller, InstructionStatementSyntax Call)> Callers(
        ProgramAnalysis analysis, Symbol routine, Func<string, string> uriOf)
    {
        var found = new List<(Protocol.ProcessorCaller Caller, InstructionStatementSyntax Call)>();
        foreach (var file in analysis.Files)
        {
            foreach (var region in file.Flow.Regions)
            {
                foreach (var block in region.Blocks)
                {
                    if (block.Steps is not [.., { On: null, Statement: InstructionStatementSyntax call }]
                        || ReturnBytes(call) is null || !block.Calls.Any(callee => Same(callee, routine)))
                    {
                        continue;
                    }
                    var at = new Protocol.Location(uriOf(call.Tree.Path), Lsp.ToRange(call.Tree, call.Span));
                    if (!found.Any(each => Same(each.Caller.At, at)))
                        found.Add((new Protocol.ProcessorCaller(region.Routine.DisplayName, at), call));
                }
            }
        }
        return found;
    }

    /// <summary>Returns how many bytes of return address a call pushes, or null for an instruction that is not a call.</summary>
    private static int? ReturnBytes(InstructionStatementSyntax call) => call.MnemonicKind switch
    {
        MnemonicKind.Jsr => 2,
        MnemonicKind.Jsl => 3,
        _ => null,
    };

    /// <summary>
    /// Checks whether two symbols are the same declaration. After an edit, the models kept for files
    /// the edit did not touch may hold a different object for the same routine, so symbols are
    /// compared by where they are declared.
    /// </summary>
    private static bool Same(Symbol a, Symbol b) =>
        a.Tree.Path == b.Tree.Path && a.NameSpan.Start == b.NameSpan.Start && a.Name == b.Name;

    /// <summary>Checks whether two locations name the same call, which is one line of one document.</summary>
    private static bool Same(Protocol.Location a, Protocol.Location b) =>
        a.Uri == b.Uri && a.Range.Start.Line == b.Range.Start.Line;

    /// <summary>Formats the mode and the widths of a state as the <c>state</c> row shows them.</summary>
    private static string Widths(ProcessorState processor) => string.Join(", ",
        ProcessorState.Format(processor.E),
        ProcessorState.Format(StateRegister.A, processor.A),
        ProcessorState.Format(StateRegister.Index, processor.Index));

    /// <summary>Returns a row with no detail, no target and no rows under it.</summary>
    private static Protocol.ProcessorRow Row(string key, string value, string? detail = null) =>
        new(key, value, detail, null, null);
}
