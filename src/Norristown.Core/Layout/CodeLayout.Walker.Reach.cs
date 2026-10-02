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
        /// processor-state analysis checks.
        /// </summary>
        private void CheckReach(SyntaxToken mnemonic, SyntaxNode? operand, AddressingMode mode)
        {
            var here = model.Segments.SpaceOf(segment);
            if (here is { HoldsCode: false } && segment is not null && routine is not null
                && codeInData.Add((routine, segment)))
            {
                Report(mnemonic, Catalogue.CodeInADataSpace.Message(segment, here.Name));
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
            {
                foreach (var (name, segmentName) in Named(expression))
                {
                    if (Unseen(mnemonic.Text, mnemonic.MnemonicKind, name, segmentName, transfer, mode != AddressingMode.Long)
                        is { } message)
                    {
                        Report(expression, message);
                    }
                }
            }
        }

        /// <summary>
        /// Checks a long branch such as <c>jeq</c>, which is a near transfer however it is written,
        /// against what its segment can reach.
        /// </summary>
        private void CheckReach(SyntaxToken mnemonic, SyntaxNode target)
        {
            foreach (var (name, segmentName) in Named(target))
            {
                if (Unseen(mnemonic.Text, MnemonicKind.Beq, name, segmentName, transfer: true, near: true) is { } message)
                    Report(target, message);
            }
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
                        ? Catalogue.TransferToAnotherSpace.Message(text, name, there, to, from)
                        : Catalogue.OperandInAnotherSpace.Message(name, there, to, from);
                case SegmentReach.NeverMapped:
                    var (fromArea, toArea) = segments.Find(segment!)!.Excluding(segments.Find(there)!)!.Value;
                    return Catalogue.SegmentNotVisible.Message(
                        transfer ? $"`{text}` targets {name}" : name, there, segment!, toArea.Config, toArea.Area, fromArea.Area);
                case SegmentReach.OtherBank when transfer && near && cpu == Cpu.Wdc65816:
                    // A conditional branch has no long form to reach with, so what reaches the
                    // other bank is a `jml` the opposite branch skips.
                    var reaches = kind switch
                    {
                        MnemonicKind.Jsr => "use `jsl`",
                        MnemonicKind.Jmp or MnemonicKind.Bra or MnemonicKind.Brl => "use `jml`",
                        _ => "a branch cannot leave its bank, so branch the other way around a `jml` to it",
                    };
                    return Catalogue.JumpLeavesBank.Message(
                        text,
                        StateValue.Hex(segments.Find(segment!)!.Bank!.Value, 2),
                        name,
                        there,
                        StateValue.Hex(segments.Find(there)!.Bank!.Value, 2),
                        reaches);
                default:
                    return null;
            }
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
