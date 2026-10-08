using System.Globalization;
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
/// </summary>
public static class Suggestions
{
    /// <summary>
    /// Returns the suggestions for <paramref name="file"/>, in the order they are reported.
    /// <paramref name="omitted"/> holds the branches of the file that this build leaves out, and
    /// <paramref name="readsCallerStack"/> says whether a routine depends on the depth of the stack
    /// it was entered with.
    /// </summary>
    public static IReadOnlyList<Diagnostic> For(
        FileAnalysis file, IReadOnlyList<TextSpan> omitted, Func<Symbol, bool> readsCallerStack)
    {
        var regions = file.Flow.Regions.Where(region => Unconditional(region, omitted)).ToList();
        var found = new List<Diagnostic>();
        found.AddRange(TailCalls(file, regions, readsCallerStack));
        if (file.State is { } states)
            found.AddRange(RedundantWidths(file, regions, states));
        if (file.Flow.Flags is { } flags)
        {
            found.AddRange(ProvedBranches(file, regions, flags));
            found.AddRange(JumpsAsBranches(file, regions, flags));
            found.AddRange(CarrySetups(file, regions, flags));
            var liveness = FlagLiveness.Of(file.Model, file.Layout, file.Flow, regions);
            found.AddRange(ZeroCompares(file, regions, flags, liveness));
            found.AddRange(Loads(file, regions, flags, liveness));
        }
        found.AddRange(BranchesOverJumps(file, regions));
        return Norristown.Diagnostics.Ordered(found);
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
        FileAnalysis file, IReadOnlyList<FlowRegion> regions, Func<Symbol, bool> readsCallerStack)
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
                    || !Own(model, calling)
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
                    || !Own(model, returning)
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
        FileAnalysis file, IReadOnlyList<FlowRegion> regions, StateAnalysis states)
    {
        var model = file.Model;
        var redundant = new Dictionary<InstructionStatementSyntax, (long Flags, StatusFlags Unneeded)>();
        foreach (var step in regions.SelectMany(region => region.Blocks).SelectMany(block => block.Steps))
        {
            if (step.Statement is not InstructionStatementSyntax { MnemonicKind: MnemonicKind.Rep or MnemonicKind.Sep } statement
                || !Own(model, step)
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
        FileAnalysis file, IReadOnlyList<FlowRegion> regions, FlagAnalysis flags)
    {
        var model = file.Model;
        var seen = new HashSet<SyntaxNode>();
        foreach (var block in regions.SelectMany(region => region.Blocks))
        {
            if (block.Steps is not [.., { Statement: InstructionStatementSyntax branch } step]
                || !Own(model, step) || !seen.Add(branch) || flags.ProvedAt(step) is not { } proved
                || flags.Before(step)?.IsBacked(proved.Flag) != true)
            {
                continue;
            }
            var text = branch.GetText().Trim();
            if (proved.Taken && block.Next is { QuestionToken: null, ReturnToken: null, Targets.Count: 1 } next
                && next.Tree == model.Tree)
            {
                yield return new Diagnostic(next.Tree.GetSpan(next.Span), Catalogue.NextProved.Message(text, proved.Why))
                {
                    Fix = new DiagnosticFix(FixKind.Redundant),
                    IsUnnecessary = true,
                };
            }
            else if (!proved.Taken && block.Next is null)
            {
                yield return new Diagnostic(branch.Tree.GetSpan(branch.Span), Catalogue.BranchNeverTaken.Message(text, proved.Why))
                {
                    Fix = new DiagnosticFix(FixKind.Redundant),
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
        FileAnalysis file, IReadOnlyList<FlowRegion> regions, FlagAnalysis flags)
    {
        var model = file.Model;
        var layout = file.Layout;
        var always = Instructions.Has(layout.Cpu, MnemonicKind.Bra);
        var seen = new HashSet<SyntaxNode>();
        foreach (var step in regions.SelectMany(region => region.Blocks).SelectMany(block => block.Steps))
        {
            if (step.Statement is not InstructionStatementSyntax { MnemonicKind: MnemonicKind.Jmp } jump
                || !Own(model, step) || !seen.Add(jump) || file.Flow.Patched.Contains(step.Key)
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
            if (always)
            {
                branch = "bra";
            }
            else if (flags.Before(step) is { } state && Known(state) is { } flag)
            {
                var value = state.ValueOf(flag)!.Value;
                branch = SyntaxFacts.TextOf(FlagAnalysis.BranchWhen(flag, value));
                why = $", because {FlagState.Describe(flag, value)}";
            }
            else
            {
                continue;
            }
            var operand = jump.Operand!.GetText().Trim();
            yield return new Diagnostic(jump.Tree.GetSpan(jump.Span),
                Catalogue.JumpAsBranch.Message(jump.GetText().Trim(), $"{branch} {operand}", why))
            {
                Fix = new DiagnosticFix(FixKind.Branch, branch),
            };
        }

        // The carry comes first, since it is the flag code most often sets on purpose.
        static StatusFlags? Known(FlagState state) =>
            new[] { StatusFlags.Carry, StatusFlags.Zero, StatusFlags.Negative, StatusFlags.Overflow }
                .Where(flag => state.ValueOf(flag) is not null && state.IsFirm(flag))
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
        FileAnalysis file, IReadOnlyList<FlowRegion> regions, FlagAnalysis flags)
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
                    || !Own(model, step) || !seen.Add(setup) || file.Flow.Patched.Contains(step.Key)
                    || flags.Before(step) is not { } before || !before.IsFirm(StatusFlags.Carry)
                    || before.ValueOf(StatusFlags.Carry) is not { } carry)
                {
                    continue;
                }
                var sets = setup.MnemonicKind == MnemonicKind.Sec;
                var text = setup.GetText().Trim();
                if (carry == sets)
                {
                    yield return new Diagnostic(setup.Tree.GetSpan(setup.Span), Catalogue.CarryAlreadySet.Message(text, sets ? 1 : 0))
                    {
                        Fix = new DiagnosticFix(FixKind.Redundant),
                        IsUnnecessary = true,
                    };
                    continue;
                }

                var uses = sets ? MnemonicKind.Sbc : MnemonicKind.Adc;
                if (RegisterWalk.NextOf(block, i) is not { Statement: InstructionStatementSyntax arithmetic } next
                    || arithmetic.MnemonicKind != uses || !Own(model, next) || file.Flow.Patched.Contains(next.Key)
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
                        text, arithmetic.GetText().Trim(), $"{SyntaxFacts.TextOf(uses)} #{folded}", carry ? 1 : 0))
                {
                    Fix = new DiagnosticFix(FixKind.CarryFolded, folded, setup.Tree.GetSpan(setup.Span)),
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
        FileAnalysis file, IReadOnlyList<FlowRegion> regions, FlagAnalysis flags, FlagLiveness liveness)
    {
        var model = file.Model;
        var seen = new HashSet<SyntaxNode>();
        foreach (var step in regions.SelectMany(region => region.Blocks).SelectMany(block => block.Steps))
        {
            if (step.Statement is not InstructionStatementSyntax compare
                || FlagAnalysis.Compared(compare.MnemonicKind) is not { } register
                || !Removable(file, step) || !seen.Add(compare)
                || StepOperands.Immediate(model, file.Layout, step) != 0
                || flags.Before(step) is not { } before || (before.Held.NzFrom & register) == 0)
            {
                continue;
            }
            string why;
            if (before.ValueOf(StatusFlags.Carry) == true && before.IsFirm(StatusFlags.Carry))
                why = "C is already 1";
            else if ((liveness.After(step) & StatusFlags.Carry) == 0)
                why = "nothing reads the C it sets";
            else
                continue;
            yield return new Diagnostic(compare.Tree.GetSpan(compare.Span),
                Catalogue.ZeroCompare.Message(compare.GetText().Trim(), RegisterEffects.Format(register), why))
            {
                Fix = new DiagnosticFix(FixKind.Redundant),
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
        FileAnalysis file, IReadOnlyList<FlowRegion> regions, FlagAnalysis flags, FlagLiveness liveness)
    {
        var model = file.Model;
        var layout = file.Layout;
        var seen = new HashSet<SyntaxNode>();
        foreach (var step in regions.SelectMany(region => region.Blocks).SelectMany(block => block.Steps))
        {
            if (step.Statement is not InstructionStatementSyntax load
                || Loaded(load.MnemonicKind) is not { } register
                || !Removable(file, step) || !seen.Add(load)
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
                string why;
                if (before.ValueOf(StatusFlags.Negative) == after.ValueOf(StatusFlags.Negative)
                    && before.ValueOf(StatusFlags.Zero) == after.ValueOf(StatusFlags.Zero)
                    && before.IsFirm(StatusFlags.Negative) && before.IsFirm(StatusFlags.Zero))
                {
                    why = "N and Z already say what it would";
                }
                else if ((liveness.After(step) & nz) == 0)
                    why = "nothing reads the N and Z it sets";
                else
                    continue;
                yield return new Diagnostic(load.Tree.GetSpan(load.Span), Catalogue.LoadAlreadyHeld.Message(text, name, hex, why))
                {
                    Fix = new DiagnosticFix(FixKind.Redundant),
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
    private static bool Removable(FileAnalysis file, Step step) =>
        Own(file.Model, step) && !file.Flow.Patched.Contains(step.Key) && !file.Layout.IsEnteredInside(step);

    /// <summary>
    /// Returns a suggestion for each conditional branch over a <c>jmp</c>, where the opposite
    /// branch to the jump's target can do both in one instruction. The <c>jmp</c> has to stand
    /// alone between the branch and the label the branch goes to, with nothing else reaching it.
    /// </summary>
    private static IEnumerable<Diagnostic> BranchesOverJumps(FileAnalysis file, IReadOnlyList<FlowRegion> regions)
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
                    || !Own(model, branching) || file.Flow.Patched.Contains(branching.Key)
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
                    || !Own(model, jumping) || file.Flow.Patched.Contains(jumping.Key)
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
}
