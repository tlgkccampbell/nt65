using Norristown.Layout;
using Norristown.Processor;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// Reads the operand an instruction has in one expansion of it, and the constant that operand
/// holds. In a macro body an <c>operand</c> parameter stands for the whole operand the call gave,
/// so every analysis that reads a constant from an instruction reads it here. That keeps the
/// width, register and loop analyses in agreement inside a macro.
/// </summary>
internal static class StepOperands
{
    /// <summary>
    /// Returns the operand an instruction has in this expansion of it. Where the body names an
    /// <c>operand</c> parameter, that is the operand the call gave.
    /// </summary>
    public static SyntaxNode? Of(SemanticModel model, Step step)
    {
        var operand = (step.Statement as InstructionStatementSyntax)?.Operand;
        return Operands.Substituted(model, operand, step.On)?.Operand ?? operand;
    }

    /// <summary>
    /// Returns the value of an instruction's operand in this expansion of it, or null when it is
    /// not a constant. For example, this is the <c>c</c> of <c>rep #c</c>, <c>ldx #c</c> or
    /// <c>pea c</c>. Where the body takes <c>p + n</c> or a <c>.byteof</c> of an <c>operand</c>
    /// argument, the value is that part of the argument's value.
    /// </summary>
    public static long? Constant(SemanticModel model, Step step)
    {
        var operand = (step.Statement as InstructionStatementSyntax)?.Operand;
        if (Operands.Substituted(model, operand, step.On) is { } given)
        {
            if (given.Expression is not ExpressionSyntax argument
                || model.ValueOf(argument, step.On).AsNumber() is not { } value)
            {
                return null;
            }
            return given.ByteOf ? (value >> (int)(8 * given.Offset)) & 0xff : value + given.Offset;
        }
        return operand is not null && CodeLayout.Expression(operand) is { } expression
            ? model.ValueOf(expression, step.On).AsNumber()
            : null;
    }

    /// <summary>
    /// Returns the value of an instruction's immediate operand in this expansion of it, or null
    /// when layout did not lay the instruction out as immediate or the value is not a constant.
    /// </summary>
    public static long? Immediate(SemanticModel model, CodeLayout layout, Step step) =>
        layout.Of(step.Statement, step.On)?.Mode == AddressingMode.Immediate ? Constant(model, step) : null;
}
