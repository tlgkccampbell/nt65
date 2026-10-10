using Norristown.Processor;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Layout;

public sealed partial class CodeLayout
{
    // This part checks what code can reach. It decides whether a segment may hold code, and what
    // code may do with a name in a segment that it cannot see, as SegmentTable.Reach decides it.
    private sealed partial class Walker
    {
        // The (routine, segment) pairs already reported for putting code in a data-only segment,
        // so that a routine with many instructions there gets the diagnostic once, not once per
        // instruction.
        private readonly HashSet<(Symbol Routine, string Segment)> codeInData = [];

        /// <summary>
        /// Checks an instruction against what its segment can reach. A segment whose space holds
        /// data may not hold instructions. A name that the code's segment cannot see is usable only
        /// as a value. An immediate may take it and data may hold it, but a jump, branch or call to
        /// it, or any other operand that addresses memory through it, is reported. A name in a
        /// segment in another home bank is reported only for a near jump, call or branch on the
        /// 65816, because a long one reaches it and data goes through the data bank, which the
        /// processor-state analysis checks. On the other CPUs, a jump, call or branch to a target
        /// placed in another bank is reported as <see cref="OutOfBank"/> decides. On the 65816, the
        /// pointer of a <c>jmp (abs)</c> or <c>jml [abs]</c> is checked as
        /// <see cref="PointerOutsideBankZero"/> decides.
        /// </summary>
        private void CheckReach(SyntaxToken mnemonic, SyntaxNode? operand, AddressingMode mode)
        {
            var here = model.Segments.SpaceOf(segment);
            if (here is { HoldsCode: false } && segment is not null && routine is not null
                && codeInData.Add((routine, segment)))
            {
                Report(mnemonic, Catalog.CodeInADataSpace.Message(segment, here.Name));
            }

            // An immediate is a value, a block move's banks are values, and `pea` and `per` push
            // their operand as a value rather than accessing memory at it.
            if (operand is null || mode is AddressingMode.Immediate or AddressingMode.BlockMove
                || mnemonic.MnemonicKind is MnemonicKind.Pea or MnemonicKind.Per)
            {
                return;
            }

            var transfer = mode is AddressingMode.Relative or AddressingMode.RelativeLong or AddressingMode.DirectRelative
                || (mode is AddressingMode.Absolute or AddressingMode.Long
                    && Instructions.Facts(mnemonic.MnemonicKind).Control is Control.Jumps or Control.Calls);
            IEnumerable<SyntaxNode> expressions = operand is AbsoluteOperandSyntax { Second: { } second }
                ? [Expression(operand)!, second]
                : Expression(operand) is { } only ? [only] : [];
            foreach (var expression in expressions)
                CheckExpression(mnemonic.Text, mnemonic.MnemonicKind, expression, transfer, mode);
        }

        /// <summary>
        /// Checks a long branch such as <c>jeq</c>, which is a near transfer however it is written,
        /// against what its segment can reach. The message suggests no long form, as it suggests
        /// none for the conditional branch the long branch stands for.
        /// </summary>
        private void CheckReach(SyntaxToken mnemonic, SyntaxNode target) =>
            CheckExpression(mnemonic.Text, MnemonicKind.Beq, target, transfer: true, AddressingMode.Relative);

        /// <summary>
        /// Checks one expression of an operand against what the code's segment can reach. Each
        /// name it holds that the segment cannot see is reported. Where none is, a
        /// <paramref name="transfer"/> is checked for a target in another bank, and a pointer in
        /// <paramref name="mode"/> for a pointer outside bank zero.
        /// </summary>
        private void CheckExpression(string text, MnemonicKind kind, SyntaxNode expression, bool transfer, AddressingMode mode)
        {
            var reported = false;
            foreach (var (name, segmentName) in Named(expression))
            {
                if (Unseen(text, kind, name, segmentName, transfer, mode != AddressingMode.Long) is { } message)
                {
                    Report(expression, message);
                    reported = true;
                }
            }
            if (transfer && !reported && OutOfBank(text, expression) is { } outOfBank)
                Report(expression, outOfBank);
            if (!reported && PointerOutsideBankZero(text, expression, mode) is { } outside)
                Report(expression, outside);
        }

        /// <summary>
        /// Returns the diagnostic for reaching <paramref name="name"/> in <paramref name="there"/>
        /// from code in the current segment, or null when the code can reach it. A
        /// <paramref name="transfer"/> jumps, calls or branches to the name, and anything else
        /// addresses memory through it. A <paramref name="near"/> transfer stays in its bank.
        /// <paramref name="kind"/> chooses the long form a message suggests, and a conditional
        /// branch has none.
        /// </summary>
        private DiagnosticMessage? Unseen(string text, MnemonicKind kind, string name, string there, bool transfer, bool near)
        {
            var segments = model.Segments;
            switch (segments.Reach(segment, there))
            {
                case SegmentReach.OtherSpace:
                    var from = AddressSpace.Format(segments.SpaceOf(segment)?.Name);
                    var to = AddressSpace.Format(segments.SpaceOf(there)?.Name);
                    return transfer
                        ? Catalog.TransferToAnotherSpace.Message(name, to, text)
                        : Catalog.OperandInAnotherSpace.Message(name, there, to, from);
                case SegmentReach.NeverMapped:
                    var (fromArea, toArea) = segments.Find(segment!)!.Excluding(segments.Find(there)!)!.Value;
                    return Catalog.SegmentNotVisible.Message(
                        transfer ? $"`{text}` targets {name}" : name, there, segment!, toArea.Config, toArea.Area, fromArea.Area);
                case SegmentReach.OtherBank when transfer && near && cpu == Cpu.Wdc65816:
                    // A conditional branch has no long form to reach with, so what reaches the
                    // other bank is a `jml` the opposite branch skips.
                    var reaches = kind switch
                    {
                        MnemonicKind.Jsr => "use `jsl`",
                        MnemonicKind.Jmp or MnemonicKind.Bra or MnemonicKind.Brl => "use `jml`",
                        _ => "branch the other way around a `jml` to it",
                    };
                    return Catalog.JumpLeavesBank.Message(
                        text,
                        name,
                        StateValue.Hex(segments.Find(there)!.Bank!.Value, 2),
                        StateValue.Hex(segments.Find(segment!)!.Bank!.Value, 2),
                        reaches);
                default:
                    return null;
            }
        }

        /// <summary>
        /// Returns the diagnostic for a jump, call or branch, written <paramref name="text"/>, to
        /// <paramref name="target"/> on a CPU with only 16-bit addresses, or null when the code can
        /// reach the target. It is reported when the target's linked range, as
        /// <see cref="LinkRange"/> bounds it, lies wholly in one bank other than <c>$00</c>, and
        /// no area the code's segment runs in reaches that bank.
        /// </summary>
        /// <remarks>
        /// The processor keeps only the address within the bank, so it reaches a target in
        /// another bank only at whatever is mapped at that address. A target in bank <c>$00</c> is
        /// at the address the processor uses, so code in any bank reaches it. A target or a
        /// segment whose placement nt65 cannot bound is not reported, because the linker decides
        /// where it lands and nothing declared says otherwise.
        /// </remarks>
        private DiagnosticMessage? OutOfBank(string text, SyntaxNode target)
        {
            if (cpu == Cpu.Wdc65816 || segment is null || model.Segments.Find(segment) is not { } here
                || LinkRange.Of(model, target, expansion) is not { } range)
            {
                return null;
            }
            var bank = range.Low >> 16;
            if (bank <= 0 || range.High >> 16 != bank
                || LinkRange.AreasOf(here) is not { Count: > 0 } areas
                || areas.Any(area => area.First >> 16 <= bank && area.Last >> 16 >= bank))
            {
                return null;
            }
            return Catalog.TargetInAnotherBank.Message(text, $"`{target.GetText().Trim()}`", StateValue.Hex(bank, 2));
        }

        /// <summary>
        /// Returns the diagnostic for a <c>jmp (abs)</c> or <c>jml [abs]</c>, written
        /// <paramref name="text"/> in <paramref name="mode"/>, whose <paramref name="pointer"/> is
        /// placed outside bank <c>$00</c> on the 65816, or null when the processor reads the
        /// pointer where it is placed. It is reported when the pointer's linked range, as
        /// <see cref="LinkRange"/> bounds it, is not wholly in bank <c>$00</c> and some segment
        /// the pointer names, or none, is not visible in bank <c>$00</c>.
        /// </summary>
        /// <remarks>
        /// These two forms read the pointer from bank <c>$00</c> whatever the data bank and the
        /// program bank are, and the output keeps the pointer's address within its bank so that it
        /// links. A segment whose declared <c>bank</c> or <c>mirrors</c> make it visible in bank
        /// <c>$00</c> is at that address there too. A pointer whose placement nt65 cannot bound is
        /// not reported, because the linker decides where it lands. Neither is one whose range
        /// starts below zero, which names no bank.
        /// </remarks>
        private DiagnosticMessage? PointerOutsideBankZero(string text, SyntaxNode pointer, AddressingMode mode)
        {
            if (cpu != Cpu.Wdc65816 || mode is not (AddressingMode.AbsoluteIndirect or AddressingMode.AbsoluteIndirectLong)
                || LinkRange.Of(model, pointer, expansion) is not { } range
                || range.Low < 0 || range.High <= 0xffff)
            {
                return null;
            }
            var named = Named(pointer).Select(found => model.Segments.Find(found.Segment)).ToList();
            if (named.Count > 0 && named.All(segment => segment?.IsSeenFrom(0) == true))
                return null;
            var low = range.Low >> 16;
            var high = range.High >> 16;
            var banks = low == high
                ? "bank " + StateValue.Hex(low, 2)
                : $"banks {StateValue.Hex(low, 2)}-{StateValue.Hex(high, 2)}";
            var written = pointer.GetText().Trim();
            var fix = mode == AddressingMode.AbsoluteIndirect
                ? $"place it in bank $00 or in memory mirrored there, or place it in this code's bank and use `jmp ({written},x)` with X at 0, which reads the pointer from the program bank"
                : "place it in bank $00 or in memory mirrored there";
            return Catalog.PointerOutsideBankZero.Message(text, $"`{written}`", banks, fix);
        }

        /// <summary>
        /// Returns the addresses an expression refers to, each with the segment it is in. They are
        /// every symbol with a segment that the expression names, and every <c>.runof(SEGMENT)</c>
        /// call, whose run address is in SEGMENT.
        /// </summary>
        private IEnumerable<(string Name, string Segment)> Named(SyntaxNode expression)
        {
            foreach (var symbol in AddressSymbols.In(model, expression, expansion))
            {
                if (symbol.Segment is { } segment)
                    yield return ($"`{symbol.QualifiedName}`", segment);
            }
            foreach (var call in new[] { expression }.Concat(expression.DescendantNodes()).OfType<CallExpressionSyntax>())
            {
                if (SegmentFunctions.Runs(call) is { } named)
                    yield return ($"`{call.GetText().Trim()}`", named);
            }
        }
    }
}
