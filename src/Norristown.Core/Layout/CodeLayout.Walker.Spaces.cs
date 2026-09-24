using Norristown.Processor;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Layout;

public sealed partial class CodeLayout
{
    // This part checks address spaces. It decides whether a segment may hold code, and what code
    // may do with a name whose segment is in a different address space from the code's own.
    private sealed partial class Walker
    {
        // The (routine, segment) pairs already reported for putting code in a data-only segment,
        // so that a routine with many instructions there gets the diagnostic once, not once per
        // instruction.
        private readonly HashSet<(Symbol Routine, string Segment)> codeInData = [];

        /// <summary>
        /// Checks an instruction against the address spaces. A segment whose space holds data may
        /// not hold instructions. A name in a different space from the code's is usable only as a
        /// value. An immediate may take it and data may hold it, but a jump, branch or call to it, or
        /// any other operand that addresses memory through it, is reported. That address belongs to
        /// another processor's memory, not to the memory this code runs in.
        /// </summary>
        private void CheckSpaces(SyntaxToken mnemonic, SyntaxNode? operand, AddressingMode mode)
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
                    var there = model.Segments.SpaceOf(segmentName);
                    if (there?.Name == here?.Name)
                        continue;
                    Report(expression, transfer
                        ? Catalogue.TransferToAnotherSpace.Message(
                            mnemonic.Text, name, segmentName, AddressSpace.Format(there?.Name), AddressSpace.Format(here?.Name))
                        : Catalogue.OperandInAnotherSpace.Message(
                            name, segmentName, AddressSpace.Format(there?.Name), AddressSpace.Format(here?.Name)));
                }
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
