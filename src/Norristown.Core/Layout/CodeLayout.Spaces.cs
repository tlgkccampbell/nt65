using Norristown.Processor;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Layout;

// Address spaces: which code may stand in a segment, and what code may do with a name that
// is in another space than its own.
public sealed partial class CodeLayout
{
    // The routines already told that a segment they place code in holds data, so that a
    // routine of many instructions hears it once.
    private readonly HashSet<(Symbol Routine, string Segment)> codeInData = [];

    /// <summary>
    /// An instruction checked against the address spaces. A segment in a space that holds data
    /// holds no instructions. A name in another space than the code's is a value there: an
    /// immediate may take it and data may hold it, but a jump, a branch or a call to it goes
    /// where another processor's memory is not, and so does an operand that reaches it.
    /// </summary>
    private void CheckSpaces(SyntaxToken mnemonic, SyntaxNode? operand, AddressingMode mode)
    {
        var here = model.Segments.SpaceOf(segment);
        if (here is { HoldsCode: false } && segment is not null && routine is not null
            && codeInData.Add((routine, segment)))
        {
            Report(mnemonic, Catalogue.CodeInADataSpace.Says(segment, here.Name));
        }

        // An immediate is a value, a block move's banks are values, and `pea` and `per` push
        // one rather than reaching anything.
        if (operand is null || mode is AddressingMode.Immediate or AddressingMode.BlockMove
            || mnemonic.Text.ToLowerInvariant() is "pea" or "per")
        {
            return;
        }

        var transfer = mode is AddressingMode.Relative or AddressingMode.RelativeLong or AddressingMode.DirectRelative
            || (mode is AddressingMode.Absolute or AddressingMode.Long
                && mnemonic.Text.ToLowerInvariant() is "jmp" or "jsr" or "jml" or "jsl");
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
                    ? Catalogue.TransferToAnotherSpace.Says(
                        mnemonic.Text, name, segmentName, AddressSpace.Spell(there?.Name), AddressSpace.Spell(here?.Name))
                    : Catalogue.OperandInAnotherSpace.Says(
                        name, segmentName, AddressSpace.Spell(there?.Name), AddressSpace.Spell(here?.Name)));
            }
        }
    }

    /// <summary>
    /// The addresses an expression names, each with the segment it is in: the symbols it
    /// names, and the run address a <c>.runof(SEGMENT)</c> stands for, which is in that segment.
    /// </summary>
    private IEnumerable<(string Name, string Segment)> Named(SyntaxNode expression)
    {
        foreach (var symbol in AddressSymbols.In(model, expression, expansion))
        {
            if (symbol.Segment is { } placed)
                yield return ($"`{symbol.QualifiedName}`", placed);
        }
        foreach (var call in new[] { expression }.Concat(expression.DescendantNodes()).OfType<CallExpressionSyntax>())
        {
            if (SegmentFunctions.Runs(call) is { } named)
                yield return ($"`{call.GetText().Trim()}`", named);
        }
    }
}
