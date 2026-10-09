using System.Globalization;
using System.Runtime.CompilerServices;
using Norristown.Layout;
using Norristown.Processor;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// Finds the places in a file where the same code could be smaller or faster, for an editor to
/// suggest. Nothing here is wrong, so no build reports it. Each suggestion carries the fix that
/// makes the change, and is offered only on a line of the file itself outside every expansion,
/// because a macro body's line serves every call and may be needed by another.
/// <para>
/// The analysis sees only the branches this build takes. A routine with a branch this build
/// leaves out gets no suggestions, because another build may take that branch and need the code.
/// </para>
/// <para>
/// A routine with a line that does not parse gets no suggestions either. What the analysis knows
/// at one line depends on every line before it, and a half-typed line may stand for code that
/// would change it.
/// </para>
/// </summary>
public static class Suggestions
{
    // The suggestions other than tail calls that each file was last found to have, by the layout
    // they were found in. A file that an edit leaves alone keeps its layout, so its suggestions
    // are not looked for again unless what they depend on has changed.
    private static readonly ConditionalWeakTable<CodeLayout, Found> foundByLayout = new();

    // The code bytes each file names, by the layout they were found in.
    private static readonly ConditionalWeakTable<CodeLayout, Named> namedByLayout = new();

    /// <summary>
    /// Returns the suggestions for <paramref name="file"/>, in the order they are reported.
    /// <paramref name="omitted"/> holds the branches of the file that this build leaves out, and
    /// <paramref name="readsCallerStack"/> says whether a routine depends on the depth of the stack
    /// it was entered with. <paramref name="named"/> holds the code bytes the program names other
    /// than as where control goes, as <see cref="NamedBytes"/> finds them.
    /// </summary>
    public static IReadOnlyList<Diagnostic> For(
        FileAnalysis file, IReadOnlyList<TextSpan> omitted, Func<Symbol, bool> readsCallerStack,
        IReadOnlyCollection<NamedByte> named)
    {
        // Only a routine whose branches the build all takes, and whose lines all parse, is asked.
        List<FlowRegion> regions = [.. file.Flow.Regions.Where(region =>
            (omitted.Count == 0 || Unconditional(region, omitted)) && !Broken(region))];
        var readAsData = ReadAsData(file, named);
        return Norristown.Diagnostics.Ordered(
            [.. TailCalls(file, readAsData, regions, readsCallerStack), .. OfTheFile(file, readAsData, regions, omitted)]);
    }

    /// <summary>
    /// Returns each code byte that an operand or a data value of <paramref name="files"/> names
    /// other than as where control goes, as in <c>lda @op+1</c>. A suggestion that changed the
    /// instruction holding such a byte would change what that code reads. Each label is the one
    /// <paramref name="current"/> gives, as for a file not analyzed again after an edit.
    /// </summary>
    public static IReadOnlyList<NamedByte> NamedBytes(IReadOnlyList<FileAnalysis> files, Func<Symbol, Symbol> current)
    {
        var named = new List<NamedByte>();
        foreach (var file in files)
        {
            if (!namedByLayout.TryGetValue(file.Layout, out var found) || found.Model != file.Model)
            {
                found = new Named(file.Model, NamedIn(file));
                namedByLayout.AddOrUpdate(file.Layout, found);
            }
            named.AddRange(found.Bytes.Select(each => each with { Label = current(each.Label) }));
        }
        return named;
    }

    /// <summary>
    /// Returns the suggestions for <paramref name="file"/> other than tail calls, looking for them
    /// again only where something they depend on has changed. They depend on the file's own
    /// analysis, the branches the build leaves out, the flags each routine the file calls reads,
    /// and which of its instructions the program reads as data. A tail call depends on more of the
    /// program than that, so it is always looked for.
    /// </summary>
    private static IReadOnlyList<Diagnostic> OfTheFile(
        FileAnalysis file, IReadOnlySet<StepKey> readAsData, IReadOnlyList<FlowRegion> regions, IReadOnlyList<TextSpan> omitted)
    {
        var read = FlagLiveness.ReadByCalls(file.Flow, regions);
        if (foundByLayout.TryGetValue(file.Layout, out var known)
            && known.Model == file.Model && known.State == file.State && known.Flags == file.Flow.Flags
            && known.Omitted.SequenceEqual(omitted)
            && (known.Read is null ? read is null : read is not null && known.Read.SequenceEqual(read))
            && known.ReadAsData.SetEquals(readAsData))
        {
            return known.Suggestions;
        }

        var found = new List<Diagnostic>();
        if (file.State is { } states)
            found.AddRange(RedundantWidths(file, readAsData, regions, states));
        if (file.Flow.Flags is { } flags)
        {
            found.AddRange(ProvedBranches(file, readAsData, regions, flags));
            found.AddRange(JumpsAsBranches(file, readAsData, regions, flags));
            found.AddRange(CarrySetups(file, readAsData, regions, flags));
            var liveness = FlagLiveness.Of(file.Model, file.Layout, file.Flow, regions);
            found.AddRange(ZeroCompares(file, readAsData, regions, flags, liveness));
            found.AddRange(Loads(file, readAsData, regions, flags, liveness));
        }
        found.AddRange(BranchesOverJumps(file, readAsData, regions));
        foundByLayout.AddOrUpdate(file.Layout, new Found(file.Model, file.State, file.Flow.Flags, omitted, read, readAsData, found));
        return found;
    }

    /// <summary>
    /// Returns whether none of the branches this build leaves out lies within the routine's own
    /// lines.
    /// </summary>
    private static bool Unconditional(FlowRegion region, IReadOnlyList<TextSpan> omitted)
    {
        var own = region.Blocks.SelectMany(block => block.Steps)
            .Where(step => step.On is null && step.Statement.Tree == region.Routine.Tree)
            .Select(step => step.Statement.Span)
            .ToList();
        if (own.Count == 0)
            return true;
        var start = own.Min(span => span.Start);
        var end = own.Max(span => span.End);
        return !omitted.Any(span => span.Start < end && span.End > start);
    }

    /// <summary>
    /// Returns whether a line of the routine's own, from its first statement to its last, has a
    /// syntax error.
    /// </summary>
    private static bool Broken(FlowRegion region)
    {
        var tree = region.Routine.Tree;
        if (!tree.Root.ContainsDiagnostics)
            return false;
        var own = region.Blocks.SelectMany(block => block.Steps)
            .Where(step => step.On is null && step.Statement.Tree == tree)
            .Select(step => step.Statement.LineIndex)
            .ToList();
        return own.Count > 0 && tree.LinesContainDiagnostics(own.Min(), own.Max());
    }

    /// <summary>
    /// Returns a suggestion for each <c>jsr</c> directly followed by <c>rts</c>, or <c>jsl</c> by
    /// <c>rtl</c>. A jump to the routine does the same, because the routine's own return then goes
    /// straight to this routine's caller. The return is removed where nothing else reaches it, and
    /// kept where a label or another path does.
    /// <para>
    /// A jump leaves the stack one return address shallower than a call does. The suggestion is
    /// therefore made only where this routine has nothing of its own on the stack at the call, and
    /// where the routine called does not depend on the depth of the stack it was entered with.
    /// </para>
    /// </summary>
    private static IEnumerable<Diagnostic> TailCalls(
        FileAnalysis file, IReadOnlySet<StepKey> readAsData, IReadOnlyList<FlowRegion> regions, Func<Symbol, bool> readsCallerStack)
    {
        var model = file.Model;
        foreach (var region in regions)
        {
            var blocks = region.Blocks;
            foreach (var block in blocks)
            {
                if (!block.IsReached || block.Next is not null || block.CallsUnknown
                    || block.Calls is not [{ Signature: { } callee } target]
                    || block.Steps is not [.., { Statement: InstructionStatementSyntax call } calling]
                    || !Own(model, calling) || Fixed(file, calling, readAsData)
                    || JumpFor(call.MnemonicKind) is not { } jump
                    || callee is { IsInterrupt: true } or { NeverReturns: true } or { Inline: not null } or { Arguments: > 0 }
                    || callee.IsFar != (call.MnemonicKind == MnemonicKind.Jsl)
                    || readsCallerStack(target)
                    || file.Flow.Registers?.Before(call) is not { Stack.Depth: 0 }
                    || block.Index + 1 >= blocks.Count)
                {
                    continue;
                }

                var after = blocks[block.Index + 1];
                if (!after.IsFallenInto || after.Next is not null
                    || after.Steps.FirstOrDefault(step => step.Label is null) is not { Statement: InstructionStatementSyntax returned } returning
                    || !Own(model, returning) || Fixed(file, returning, readAsData)
                    || returned.MnemonicKind != (call.MnemonicKind == MnemonicKind.Jsl ? MnemonicKind.Rtl : MnemonicKind.Rts)
                    || !Adjacent(call, returned))
                {
                    continue;
                }

                // A return that a label or another path also reaches has to stay for them.
                var alone = after.Label is null && after.Predecessors.Count == 1;
                var saved = call.MnemonicKind == MnemonicKind.Jsl ? 10 : 9;
                var text = SyntaxFacts.TextOf(call.MnemonicKind);
                yield return new Diagnostic(
                    call.Tree.GetSpan(call.Span),
                    Catalogue.TailCall.Message(
                        $"{text} {target.DisplayName}",
                        SyntaxFacts.TextOf(returned.MnemonicKind),
                        $"{jump} {target.DisplayName}",
                        saved,
                        alone ? " and a byte" : ""))
                {
                    Fix = new DiagnosticFix(FixKind.TailCall, jump, alone ? returned.Tree.GetSpan(returned.Span) : null),
                };
            }
        }
    }

    /// <summary>
    /// Returns whether nothing but blank lines, comments and labels stands between two statements
    /// in the source. The analysis sees only the branches this build takes, and a branch another
    /// build takes may put code between them.
    /// </summary>
    private static bool Adjacent(SyntaxNode first, SyntaxNode second)
    {
        var tree = first.Tree;
        var from = tree.GetSpan(first.Span).LineIndex;
        var to = tree.GetSpan(second.Span).LineIndex;
        for (var line = from + 1; line < to; line++)
        {
            if (tree.GetLine(line).Statement is not (BlankLineSyntax or LabeledLineSyntax { Statement: null }))
                return false;
        }
        return true;
    }

    /// <summary>Returns the jump that makes a tail call in place of a call, or null for anything else.</summary>
    private static string? JumpFor(MnemonicKind mnemonic) => mnemonic switch
    {
        MnemonicKind.Jsr => "jmp",
        MnemonicKind.Jsl => "jml",
        _ => null,
    };

    /// <summary>
    /// Returns a suggestion for each <c>rep</c> or <c>sep</c> that sets a width the register
    /// already has on every path to it. One that changes nothing at all can go, and one that
    /// changes only some of what it names can name less. A statement a family's instances all
    /// share is suggested only where the width is already set in every one of them.
    /// </summary>
    private static IEnumerable<Diagnostic> RedundantWidths(
        FileAnalysis file, IReadOnlySet<StepKey> readAsData, IReadOnlyList<FlowRegion> regions, StateAnalysis states)
    {
        var model = file.Model;
        var redundant = new Dictionary<InstructionStatementSyntax, (long Flags, StatusFlags Unneeded)>();
        foreach (var step in regions.SelectMany(region => region.Blocks).SelectMany(block => block.Steps))
        {
            if (step.Statement is not InstructionStatementSyntax { MnemonicKind: MnemonicKind.Rep or MnemonicKind.Sep } statement
                || !Own(model, step) || Fixed(file, step, readAsData)
                || StepOperands.Immediate(model, file.Layout, step) is not { } flags)
            {
                continue;
            }
            var unneeded = Unneeded(
                statement.MnemonicKind == MnemonicKind.Rep, flags, states.Before(statement, step.On)?.Processor);

            // Another instance of the same statement keeps only what both find unneeded.
            redundant[statement] = redundant.TryGetValue(statement, out var earlier)
                ? earlier with { Unneeded = earlier.Unneeded & unneeded }
                : (flags, unneeded);
        }

        foreach (var (statement, (flags, unneeded)) in redundant)
        {
            if (unneeded == StatusFlags.None)
                continue;
            var text = statement.GetText().Trim();
            var why = Why(statement.MnemonicKind == MnemonicKind.Rep, unneeded);
            var left = flags & 0xff & ~(long)unneeded;
            if (left == 0)
            {
                yield return new Diagnostic(
                    statement.Tree.GetSpan(statement.Span),
                    Catalogue.WidthAlreadySet.Message(text, "changes nothing", why))
                {
                    Fix = new DiagnosticFix(FixKind.Redundant),
                    IsUnnecessary = true,
                };
                continue;
            }
            var expression = CodeLayout.Expression(statement.Operand!)!;
            var narrowed = StateValue.Hex(left, 2);
            yield return new Diagnostic(
                expression.Tree.GetSpan(expression.Span),
                Catalogue.WidthAlreadySet.Message(text, $"needs only `#{narrowed}`", why))
            {
                Fix = new DiagnosticFix(FixKind.Flags, narrowed),
            };
        }
    }

    /// <summary>
    /// Returns the width flags among <paramref name="flags"/> that a <c>rep</c> or <c>sep</c>
    /// would set to what they already are in <paramref name="state"/>. Only native mode is asked
    /// about, where a width is what M or X says. Flags other than M and X always change something,
    /// and are never unneeded.
    /// </summary>
    private static StatusFlags Unneeded(bool reset, long flags, ProcessorState? state)
    {
        if (state is not { E: ProcessorMode.Native } known)
            return StatusFlags.None;
        var target = reset ? Width.Sixteen : Width.Eight;
        var unneeded = StatusFlags.None;
        if ((flags & (long)StatusFlags.M) != 0 && known.A == target)
            unneeded |= StatusFlags.M;
        if ((flags & (long)StatusFlags.X) != 0 && known.Index == target)
            unneeded |= StatusFlags.X;
        return unneeded;
    }

    /// <summary>
    /// Returns why the <paramref name="unneeded"/> flags of a <c>rep</c> or <c>sep</c> change
    /// nothing, in the words a message uses.
    /// </summary>
    private static string Why(bool reset, StatusFlags unneeded)
    {
        var width = StateChecks.Format(reset ? Width.Sixteen : Width.Eight);
        var registers = (unneeded & (StatusFlags.M | StatusFlags.X)) switch
        {
            StatusFlags.M => "A is",
            StatusFlags.X => "X and Y are",
            _ => "A, X and Y are",
        };
        return $"{registers} already {width} here";
    }

    /// <summary>
    /// Returns a suggestion for each conditional branch that the flags show goes one way only,
    /// where that is worth saying. A <c>.next</c> that says a branch is always taken is not needed
    /// where the flags prove it, and a branch that is never taken is usually a mistake.
    /// </summary>
    private static IEnumerable<Diagnostic> ProvedBranches(
        FileAnalysis file, IReadOnlySet<StepKey> readAsData, IReadOnlyList<FlowRegion> regions, FlagAnalysis flags)
    {
        var model = file.Model;
        var seen = new HashSet<SyntaxNode>();
        foreach (var block in regions.SelectMany(region => region.Blocks))
        {
            if (block.Steps is not [.., { Statement: InstructionStatementSyntax branch } step]
                || !Own(model, step) || !seen.Add(branch) || flags.ProvedAt(step) is not { } proved
                || flags.Before(step) is not { } before || !before.IsBacked(proved.Flag))
            {
                continue;
            }
            var text = branch.GetText().Trim();
            var unpromised = Unpromised.Of(before, proved.Flag);
            if (proved.Taken && block.Next is { QuestionToken: null, ReturnToken: null, Targets.Count: 1 } next
                && next.Tree == model.Tree)
            {
                yield return new Diagnostic(next.Tree.GetSpan(next.Span),
                    Catalogue.NextProved.Message(text, proved.Why) + unpromised)
                {
                    Fix = new DiagnosticFix(FixKind.Redundant, Caveat: unpromised?.Caveat),
                    IsUnnecessary = true,
                };
            }
            else if (!proved.Taken && block.Next is null && !Fixed(file, step, readAsData))
            {
                yield return new Diagnostic(branch.Tree.GetSpan(branch.Span),
                    Catalogue.BranchNeverTaken.Message(text, proved.Why) + unpromised)
                {
                    Fix = new DiagnosticFix(FixKind.Redundant, Caveat: unpromised?.Caveat),
                    IsUnnecessary = true,
                };
            }
        }
    }

    /// <summary>
    /// Returns a suggestion for each <c>jmp</c> to a label that a branch could reach, which saves a
    /// byte. On a CPU with <c>bra</c> that branch is <c>bra</c>. On any other it is a branch on a
    /// flag known on every path to the <c>jmp</c>, the carry first.
    /// </summary>
    private static IEnumerable<Diagnostic> JumpsAsBranches(
        FileAnalysis file, IReadOnlySet<StepKey> readAsData, IReadOnlyList<FlowRegion> regions, FlagAnalysis flags)
    {
        var model = file.Model;
        var layout = file.Layout;
        var always = Instructions.Has(layout.Cpu, MnemonicKind.Bra);
        var seen = new HashSet<SyntaxNode>();
        foreach (var step in regions.SelectMany(region => region.Blocks).SelectMany(block => block.Steps))
        {
            if (step.Statement is not InstructionStatementSyntax { MnemonicKind: MnemonicKind.Jmp } jump
                || !Own(model, step) || !seen.Add(jump) || Fixed(file, step, readAsData)
                || layout.Of(jump, step.On)?.Mode != AddressingMode.Absolute
                || Targets.Of(model, Transfers.TargetOf(jump, AddressingMode.Absolute), step.On) is not { Symbol.IsAddress: true } target
                || layout.PositionOf(jump) is not { } from
                || layout.PositionOf(target.Symbol, target.At) is not { } to || to.Stream != from.Stream)
            {
                continue;
            }

            // The branch is a byte shorter than the jump, which brings a target after it a byte nearer.
            var reach = to.Offset >= from.End ? to.Offset - from.End : to.Offset - from.End + 1;
            if (reach is < -128 or > 127)
                continue;
            string branch;
            var why = "";
            Unpromised? unpromised = null;
            if (always)
            {
                branch = "bra";
            }
            else if (flags.Before(step) is { } state && Known(state) is { } flag)
            {
                var value = state.ValueOf(flag)!.Value;
                branch = SyntaxFacts.TextOf(FlagAnalysis.BranchWhen(flag, value));
                why = $", because {FlagState.Describe(flag, value)}";
                unpromised = Unpromised.Of(state, flag);
            }
            else
            {
                continue;
            }
            var operand = jump.Operand!.GetText().Trim();
            yield return new Diagnostic(jump.Tree.GetSpan(jump.Span),
                Catalogue.JumpAsBranch.Message(jump.GetText().Trim(), $"{branch} {operand}", why) + unpromised)
            {
                Fix = new DiagnosticFix(FixKind.Branch, branch, Caveat: unpromised?.Caveat),
            };
        }

        // The carry comes first, since it is the flag code most often sets on purpose. A flag
        // whose value nothing fails to promise comes before one a called routine leaves without
        // promising it.
        static StatusFlags? Known(FlagState state) =>
            new[] { StatusFlags.Carry, StatusFlags.Zero, StatusFlags.Negative, StatusFlags.Overflow }
                .Where(flag => state.ValueOf(flag) is not null && state.IsBacked(flag))
                .OrderBy(flag => Unpromised.Of(state, flag) is null ? 0 : 1)
                .Select(flag => (StatusFlags?)flag)
                .FirstOrDefault();
    }

    /// <summary>
    /// Returns a suggestion for each <c>clc</c> or <c>sec</c> that the carry makes unneeded. One
    /// that sets C to what it already is can go. One before an immediate <c>adc</c> or
    /// <c>sbc</c> that sets C to the opposite of what it is can go too, where the operand is made
    /// one less. That holds only where the operand's sign and decimal digits come out the same.
    /// </summary>
    private static IEnumerable<Diagnostic> CarrySetups(
        FileAnalysis file, IReadOnlySet<StepKey> readAsData, IReadOnlyList<FlowRegion> regions, FlagAnalysis flags)
    {
        var model = file.Model;
        var layout = file.Layout;
        var seen = new HashSet<SyntaxNode>();
        foreach (var block in regions.SelectMany(region => region.Blocks))
        {
            for (var i = 0; i < block.Steps.Count; i++)
            {
                var step = block.Steps[i];
                if (step.Statement is not InstructionStatementSyntax { MnemonicKind: MnemonicKind.Clc or MnemonicKind.Sec } setup
                    || !Own(model, step) || !seen.Add(setup) || Fixed(file, step, readAsData)
                    || flags.Before(step) is not { } before || !before.IsBacked(StatusFlags.Carry)
                    || before.ValueOf(StatusFlags.Carry) is not { } carry)
                {
                    continue;
                }
                var sets = setup.MnemonicKind == MnemonicKind.Sec;
                var text = setup.GetText().Trim();
                var unpromised = Unpromised.Of(before, StatusFlags.Carry);
                if (carry == sets)
                {
                    yield return new Diagnostic(setup.Tree.GetSpan(setup.Span),
                        Catalogue.CarryAlreadySet.Message(text, sets ? 1 : 0) + unpromised)
                    {
                        Fix = new DiagnosticFix(FixKind.Redundant, Caveat: unpromised?.Caveat),
                        IsUnnecessary = true,
                    };
                    continue;
                }

                var uses = sets ? MnemonicKind.Sbc : MnemonicKind.Adc;
                if (RegisterWalk.NextOf(block, i) is not { Statement: InstructionStatementSyntax arithmetic } next
                    || arithmetic.MnemonicKind != uses || !Own(model, next) || Fixed(file, next, readAsData)
                    || !Adjacent(setup, arithmetic)
                    || StepOperands.Immediate(model, layout, next) is not { } value
                    || CodeLayout.Expression(arithmetic.Operand!) is not { } expression
                    || !Foldable(value, layout.Of(arithmetic, next.On)?.Bits ?? 8))
                {
                    continue;
                }
                var folded = Decremented(expression, value);
                yield return new Diagnostic(expression.Tree.GetSpan(expression.Span),
                    Catalogue.CarryFolded.Message(
                        text, arithmetic.GetText().Trim(), $"{SyntaxFacts.TextOf(uses)} #{folded}", carry ? 1 : 0)
                    + unpromised)
                {
                    Fix = new DiagnosticFix(FixKind.CarryFolded, folded, setup.Tree.GetSpan(setup.Span), unpromised?.Caveat),
                };
            }
        }
    }

    /// <summary>
    /// Returns whether an immediate operand of <paramref name="bits"/> bits can be made one less
    /// with the carry it adds folded in, and still give the same result and flags. Zero and the
    /// value with only the top bit set change sign, and a low digit of 0 changes the decimal
    /// digits.
    /// </summary>
    private static bool Foldable(long value, int bits)
    {
        var n = value & ((1L << bits) - 1);
        return n != 0 && n != 1L << (bits - 1) && (n & 0xf) != 0;
    }

    /// <summary>
    /// Returns the text of an expression one less than <paramref name="value"/>, which is the
    /// expression's value. A number keeps the way it was written, and anything else has
    /// <c>-1</c> after it.
    /// </summary>
    private static string Decremented(ExpressionSyntax expression, long value)
    {
        var text = expression.GetText().Trim();
        if (expression is NumberExpressionSyntax)
        {
            var less = value - 1;
            if (text.StartsWith('$'))
            {
                var digits = less.ToString(text.Any(char.IsUpper) ? "X" : "x", CultureInfo.InvariantCulture);
                return "$" + digits.PadLeft(text.Length - 1, '0');
            }
            if (text.StartsWith('%'))
                return "%" + Convert.ToString(less, 2).PadLeft(text.Length - 1, '0');
            return less.ToString(CultureInfo.InvariantCulture);
        }
        return expression is NameExpressionSyntax ? $"{text}-1" : $"({text})-1";
    }

    /// <summary>
    /// Returns a suggestion for each <c>cmp #0</c>, <c>cpx #0</c> or <c>cpy #0</c> whose register N
    /// and Z were already set from. The compare then changes only C, which it sets to 1, so it can
    /// go where C is already 1 or nothing reads C before it changes.
    /// </summary>
    private static IEnumerable<Diagnostic> ZeroCompares(
        FileAnalysis file, IReadOnlySet<StepKey> readAsData, IReadOnlyList<FlowRegion> regions, FlagAnalysis flags, FlagLiveness liveness)
    {
        var model = file.Model;
        var seen = new HashSet<SyntaxNode>();
        foreach (var step in regions.SelectMany(region => region.Blocks).SelectMany(block => block.Steps))
        {
            if (step.Statement is not InstructionStatementSyntax compare
                || FlagAnalysis.Compared(compare.MnemonicKind) is not { } register
                || !Removable(file, step, readAsData) || !seen.Add(compare)
                || StepOperands.Immediate(model, file.Layout, step) != 0
                || flags.Before(step) is not { } before || (before.Held.NzFrom & register) == 0)
            {
                continue;
            }
            // A reason that relies on nothing a called routine leaves without promising it comes
            // first.
            string why;
            var carried = before.ValueOf(StatusFlags.Carry) == true && before.IsBacked(StatusFlags.Carry);
            var unpromised = carried ? Unpromised.Of(before, StatusFlags.Carry) : null;
            if (carried && unpromised is null)
                why = "C is already 1";
            else if ((liveness.After(step) & StatusFlags.Carry) == 0)
                (why, unpromised) = ("nothing reads the C it sets", null);
            else if (carried)
                why = "C is already 1";
            else
                continue;
            yield return new Diagnostic(compare.Tree.GetSpan(compare.Span),
                Catalogue.ZeroCompare.Message(compare.GetText().Trim(), RegisterEffects.Format(register), why)
                + unpromised)
            {
                Fix = new DiagnosticFix(FixKind.Redundant, Caveat: unpromised?.Caveat),
                IsUnnecessary = true,
            };
        }
    }

    /// <summary>
    /// Returns a suggestion for each immediate load of a constant that a register already holds.
    /// A load into a register that holds it changes nothing where N and Z already say what it
    /// would, or nothing reads them before they change. A load of a constant another register
    /// holds, or one more or less than the register holds, can be a transfer, an increment or a
    /// decrement, which is a byte shorter and sets N and Z the same way.
    /// </summary>
    private static IEnumerable<Diagnostic> Loads(
        FileAnalysis file, IReadOnlySet<StepKey> readAsData, IReadOnlyList<FlowRegion> regions, FlagAnalysis flags, FlagLiveness liveness)
    {
        var model = file.Model;
        var layout = file.Layout;
        var seen = new HashSet<SyntaxNode>();
        foreach (var step in regions.SelectMany(region => region.Blocks).SelectMany(block => block.Steps))
        {
            if (step.Statement is not InstructionStatementSyntax load
                || Loaded(load.MnemonicKind) is not { } register
                || !Removable(file, step, readAsData) || !seen.Add(load)
                || StepOperands.Immediate(model, layout, step) is not { } immediate
                || flags.Before(step) is not { } before)
            {
                continue;
            }
            var bits = layout.Of(load, step.On)?.Bits ?? 8;
            var value = immediate & ((1L << bits) - 1);
            var text = load.GetText().Trim();
            var name = RegisterEffects.Format(register);
            var hex = StateValue.Hex(value, bits / 4);
            if (before.Held.ValueOf(register) == value)
            {
                var nz = StatusFlags.Negative | StatusFlags.Zero;
                var after = before.Loaded(value, bits);
                // A reason that relies on nothing a called routine leaves without promising it
                // comes first.
                var said = before.ValueOf(StatusFlags.Negative) == after.ValueOf(StatusFlags.Negative)
                    && before.ValueOf(StatusFlags.Zero) == after.ValueOf(StatusFlags.Zero)
                    && before.IsBacked(StatusFlags.Negative) && before.IsBacked(StatusFlags.Zero);
                var unpromised = said ? Unpromised.Of(before, nz) : null;
                string why;
                if (said && unpromised is null)
                    why = "N and Z already say what it would";
                else if ((liveness.After(step) & nz) == 0)
                    (why, unpromised) = ("nothing reads the N and Z it sets", null);
                else if (said)
                    why = "N and Z already say what it would";
                else
                    continue;
                yield return new Diagnostic(load.Tree.GetSpan(load.Span),
                    Catalogue.LoadAlreadyHeld.Message(text, name, hex, why) + unpromised)
                {
                    Fix = new DiagnosticFix(FixKind.Redundant, Caveat: unpromised?.Caveat),
                    IsUnnecessary = true,
                };
                continue;
            }
            if (layout.Cpu == Cpu.Wdc65816 || Shorter(layout.Cpu, register, value, before.Held) is not { } shorter)
                continue;
            yield return new Diagnostic(load.Tree.GetSpan(load.Span),
                Catalogue.LoadFromRegister.Message(text, shorter.Instruction, shorter.Why))
            {
                Fix = new DiagnosticFix(FixKind.Instruction, shorter.Instruction),
            };
        }

        static Registers? Loaded(MnemonicKind mnemonic) => mnemonic switch
        {
            MnemonicKind.Lda => Registers.A,
            MnemonicKind.Ldx => Registers.X,
            MnemonicKind.Ldy => Registers.Y,
            _ => null,
        };
    }

    /// <summary>
    /// Returns the one-byte instruction that leaves <paramref name="value"/> in
    /// <paramref name="register"/> from what <paramref name="held"/> says the registers hold, with
    /// the words that say why, or null where none does. A transfer from another register comes
    /// first, then an increment or a decrement of the register itself.
    /// </summary>
    private static (string Instruction, string Why)? Shorter(Cpu cpu, Registers register, long value, KnownRegisters held)
    {
        var hex = StateValue.Hex(value, 2);
        foreach (var source in (Registers[])[Registers.A, Registers.X, Registers.Y])
        {
            if (source != register && held.ValueOf(source) == value && Transfer(source, register) is { } transfer)
                return (transfer, $"{RegisterEffects.Format(source)} holds {hex} here");
        }
        var name = RegisterEffects.Format(register);
        var stepped = register switch
        {
            Registers.X => ("inx", "dex"),
            Registers.Y => ("iny", "dey"),
            _ => Instructions.Modes(cpu, MnemonicKind.Inc).Contains(AddressingMode.Accumulator) ? ("inc a", "dec a") : default,
        };
        if (stepped == default || held.ValueOf(register) is not { } now)
            return null;
        if (((now + 1) & 0xff) == value)
            return (stepped.Item1, $"{name} holds {StateValue.Hex(now, 2)} here");
        if (((now - 1) & 0xff) == value)
            return (stepped.Item2, $"{name} holds {StateValue.Hex(now, 2)} here");
        return null;

        // Only the 6502's own transfers count: X and Y swap only on the 65816.
        static string? Transfer(Registers from, Registers to) => (from, to) switch
        {
            (Registers.A, Registers.X) => "tax",
            (Registers.A, Registers.Y) => "tay",
            (Registers.X, Registers.A) => "txa",
            (Registers.Y, Registers.A) => "tya",
            _ => null,
        };
    }

    /// <summary>
    /// Returns a value indicating whether a suggestion may remove or shorten the instruction at
    /// <paramref name="step"/>. It has to be a line of the file itself, one no store rewrites,
    /// and one no <c>.label</c> names a position inside.
    /// </summary>
    private static bool Removable(FileAnalysis file, Step step, IReadOnlySet<StepKey> readAsData) =>
        Own(file.Model, step) && !Fixed(file, step, readAsData);

    /// <summary>
    /// Returns a value indicating whether a suggestion must leave the bytes of the instruction at
    /// <paramref name="step"/> as they are. That holds where a store rewrites it, where a
    /// <c>.label</c> names a position inside it, and where <paramref name="readAsData"/> says code
    /// reads its bytes.
    /// </summary>
    private static bool Fixed(FileAnalysis file, Step step, IReadOnlySet<StepKey> readAsData) =>
        file.Flow.Patched.Contains(step.Key) || file.Layout.IsEnteredInside(step) || readAsData.Contains(step.Key);

    /// <summary>
    /// Returns the instructions of <paramref name="file"/> that hold a byte <paramref name="named"/>
    /// names. Where the constant added to a label is not known, the instruction at the label is
    /// the one taken.
    /// </summary>
    private static HashSet<StepKey> ReadAsData(FileAnalysis file, IReadOnlyCollection<NamedByte> named)
    {
        var layout = file.Layout;
        var bytes = new List<(int Stream, long Offset)>();
        foreach (var each in named)
        {
            if (layout.PositionOf(each.Label, each.At) is { } label)
                bytes.Add((label.Stream, label.Offset + (each.Offset ?? 0)));
        }
        var read = new HashSet<StepKey>();
        if (bytes.Count == 0)
            return read;
        foreach (var step in file.Flow.Regions.SelectMany(region => region.Blocks).SelectMany(block => block.Steps))
        {
            if (step.Statement is InstructionStatementSyntax
                && layout.PositionOf(step.Statement, step.On) is { } at
                && bytes.Any(each => each.Stream == at.Stream && each.Offset >= at.Offset && each.Offset < at.End))
            {
                read.Add(step.Key);
            }
        }
        return read;
    }

    /// <summary>
    /// Returns each label an operand or a data value of <paramref name="file"/> names other than as
    /// where control goes, with the constant added to it. A name in a <c>.next</c>, a <c>.patch</c>
    /// or another annotation is not an operand, and is left out.
    /// </summary>
    private static List<NamedByte> NamedIn(FileAnalysis file)
    {
        var model = file.Model;
        var named = new List<NamedByte>();
        foreach (var step in file.Layout.Steps)
        {
            SyntaxNode? target = null;
            if (step.Statement is InstructionStatementSyntax instruction)
            {
                var mode = file.Layout.Of(instruction, step.On)?.Mode;
                if (Transfers.Of(instruction, mode) is Transfer.Branch or Transfer.Jump or Transfer.Call)
                    target = Transfers.TargetOf(instruction, mode);
            }
            else if (step.Statement is not (DataDirectiveSyntax or DataValuesSyntax))
            {
                continue;
            }
            foreach (var name in step.Statement.DescendantNodes().OfType<NameExpressionSyntax>())
            {
                if ((target is not null && name.AncestorsAndSelf().Contains(target)) || Targets.Of(model, name, step.On) is not { } found)
                    continue;
                named.Add(new NamedByte(found.Symbol, found.At, OffsetOf(model, name, step.On)));
            }
        }
        return named;
    }

    /// <summary>
    /// Returns the constant added to or taken from <paramref name="name"/> where it stands in an
    /// expression such as <c>@op+1</c>, 0 where it stands alone, or null where it is not known.
    /// </summary>
    private static long? OffsetOf(SemanticModel model, NameExpressionSyntax name, Expansion? on)
    {
        SyntaxNode node = name;
        while (node.Parent is ParenthesizedExpressionSyntax parenthesized)
            node = parenthesized;
        if (node.Parent is not BinaryExpressionSyntax { OperatorToken.Kind: SyntaxKind.Plus or SyntaxKind.Minus } binary)
            return node.Parent is ExpressionSyntax ? null : 0;
        var minus = binary.OperatorToken.Kind == SyntaxKind.Minus;
        if (binary.Left == node)
            return model.ValueOf(binary.Right, on).AsNumber() is { } right ? (minus ? -right : right) : null;
        return !minus && model.ValueOf(binary.Left, on).AsNumber() is { } left ? left : null;
    }

    /// <summary>
    /// Returns a suggestion for each conditional branch over a <c>jmp</c>, where the opposite
    /// branch to the jump's target can do both in one instruction. The <c>jmp</c> has to stand
    /// alone between the branch and the label the branch goes to, with nothing else reaching it.
    /// </summary>
    private static IEnumerable<Diagnostic> BranchesOverJumps(FileAnalysis file, IReadOnlySet<StepKey> readAsData, IReadOnlyList<FlowRegion> regions)
    {
        var model = file.Model;
        var layout = file.Layout;
        foreach (var region in regions)
        {
            var blocks = region.Blocks;
            for (var i = 0; i + 2 < blocks.Count; i++)
            {
                var block = blocks[i];
                if (block.End != BlockEnd.Branch || block.Next is not null
                    || block.Steps is not [.., { Statement: InstructionStatementSyntax branch } branching]
                    || !Own(model, branching) || Fixed(file, branching, readAsData)
                    || FlagAnalysis.Tested(branch.MnemonicKind) is not { } tested
                    || SyntaxFacts.IsLongBranch(branch.MnemonicKind)
                    || layout.Of(branch) is not { Length: 2, Inverted: false }
                    || layout.PositionOf(branch) is not { } from)
                {
                    continue;
                }
                var over = blocks[i + 1];
                if (over.Label is not null || over.Predecessors.Count != 1 || over.Next is not null
                    || over.Steps is not [{ Statement: InstructionStatementSyntax { MnemonicKind: MnemonicKind.Jmp } jump } jumping]
                    || !Own(model, jumping) || Fixed(file, jumping, readAsData)
                    || layout.Of(jump)?.Mode != AddressingMode.Absolute
                    || Targets.Of(model, Transfers.TargetOf(jump, AddressingMode.Absolute), null) is not { Symbol.IsAddress: true } target
                    || layout.PositionOf(target.Symbol, target.At) is not { } to || to.Stream != from.Stream
                    || layout.PositionOf(jump) is not { } skipped
                    || blocks[i + 2].Label is not { } skip
                    || Targets.Of(model, Transfers.TargetOf(branch, AddressingMode.Relative), null)?.Symbol != skip

                    // The label has to stand right after the jump. One a `.label` names inside an
                    // instruction comes next among the blocks without following the jump's bytes.
                    || layout.PositionOf(skip) is not { } landed || landed.Stream != skipped.Stream || landed.Offset != skipped.End
                    || target.Symbol == skip
                    || !Adjacent(branch, jump))
                {
                    continue;
                }

                // The jump's 3 bytes go, which brings a target after them 3 bytes nearer.
                int reach;
                if (to.Offset >= skipped.End)
                    reach = to.Offset - skipped.End;
                else if (to.Offset <= from.Offset)
                    reach = to.Offset - from.End;
                else
                    continue;
                if (reach is < -128 or > 127)
                    continue;
                var opposite = SyntaxFacts.TextOf(FlagAnalysis.BranchWhen(tested.Flag, !tested.TakenWhen));
                var replaced = $"{opposite} {jump.Operand!.GetText().Trim()}";
                yield return new Diagnostic(branch.Tree.GetSpan(branch.Span),
                    Catalogue.BranchOverJump.Message(branch.GetText().Trim(), jump.GetText().Trim(), replaced))
                {
                    Fix = new DiagnosticFix(FixKind.BranchOver, replaced, jump.Tree.GetSpan(jump.Span)),
                };
            }
        }
    }

    /// <summary>
    /// Returns whether a step is a line of the file itself, outside every expansion, which is the
    /// only kind of line a suggestion can change.
    /// </summary>
    private static bool Own(SemanticModel model, Step step) => step.On is null && step.Statement.Tree == model.Tree;

    /// <summary>
    /// Represents a code byte that an operand or a data value names other than as where control
    /// goes, as the label it names and the constant added to it.
    /// </summary>
    /// <param name="Label">The label the operand or value names.</param>
    /// <param name="At">The <see cref="Expansion"/> the label belongs to, for a label a macro body declares.</param>
    /// <param name="Offset">The constant added to the label, or null where it is not known.</param>
    public readonly record struct NamedByte(Symbol Label, Expansion? At, long? Offset);

    /// <summary>
    /// Represents the words a suggestion adds where it relies on what a called routine's body
    /// leaves in a flag without the routine promising it. Such a routine declares neither the
    /// flag's value after <c>-&gt;</c> nor that it keeps the flag, so its body is inferred and
    /// used, and the suggestion says so.
    /// </summary>
    /// <param name="Message">The words the suggestion's message ends with.</param>
    /// <param name="Caveat">The words the title of the suggestion's fix ends with.</param>
    private sealed record Unpromised(string Message, string Caveat)
    {
        /// <summary>
        /// Returns <paramref name="message"/> with the words <paramref name="unpromised"/> adds at
        /// its end, or <paramref name="message"/> itself where <paramref name="unpromised"/> is null.
        /// </summary>
        public static DiagnosticMessage operator +(DiagnosticMessage message, Unpromised? unpromised) =>
            unpromised is null ? message : message with { Text = message.Text + unpromised.Message };

        /// <summary>
        /// Returns the words for what is known about <paramref name="flags"/> in
        /// <paramref name="state"/>, or null where every routine that knowledge depends on
        /// promises it. Where several routines do not, the first by name is the one named.
        /// </summary>
        public static Unpromised? Of(FlagState state, StatusFlags flags)
        {
            Symbol? routine = null;
            var named = new List<string>();
            foreach (var flag in new[] { StatusFlags.Negative, StatusFlags.Zero, StatusFlags.Carry, StatusFlags.Overflow })
            {
                if ((flags & flag) == 0)
                    continue;
                var declining = state.SourcesOf(flag)
                    .Where(source => !Promises(source, flag))
                    .OrderBy(source => source.QualifiedName, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (declining is null || (routine is not null && declining != routine))
                    continue;
                routine = declining;
                named.Add(FlagState.Name(flag));
            }
            if (routine is null)
                return null;
            var names = string.Join(" and ", named);
            var them = named.Count > 1 ? "them" : "it";
            return new Unpromised(
                $"; `{routine.DisplayName}` leaves {names} this way but does not promise {them}",
                $", though `{routine.DisplayName}` does not promise {names}");
        }

        /// <summary>
        /// Returns whether <paramref name="routine"/> declares the value it returns
        /// <paramref name="flag"/> with, or that it keeps the flag.
        /// </summary>
        private static bool Promises(Symbol routine, StatusFlags flag) =>
            FlagAnalysis.SignatureOf(routine) is { } signature
            && ((signature.ExitFlags.Known | FlagExits.FlagsOf(signature.Keeps)) & flag) != 0;
    }

    /// <summary>
    /// The suggestions other than tail calls found for one file, with what they were found from.
    /// </summary>
    private sealed record Found(
        SemanticModel Model,
        StateAnalysis? State,
        FlagAnalysis? Flags,
        IReadOnlyList<TextSpan> Omitted,
        List<StatusFlags>? Read,
        IReadOnlySet<StepKey> ReadAsData,
        List<Diagnostic> Suggestions);

    /// <summary>The code bytes one file names, with the model they were found under.</summary>
    private sealed record Named(SemanticModel Model, IReadOnlyList<NamedByte> Bytes);
}
