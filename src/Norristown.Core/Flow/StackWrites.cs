using Norristown.Layout;
using Norristown.Processor;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// Recognizes a store into the bytes on the stack, which the register walk and the
/// processor-state analysis both treat as making whatever a later pull or <c>rti</c> restores
/// unknown, because nt65 does not follow which stacked byte the store changes. A store counts
/// where it is relative to S, where it is indexed by a register that holds S, as X does after
/// <c>tsx</c>, from a base on the stack's page or one nt65 cannot work out, and where it names a
/// fixed address from $0100 to $01FF. A store through a pointer on the stack writes elsewhere, so
/// it does not count, and neither does a store to a member of a <c>.frame</c>, whose bytes the
/// routine laid out for itself to write.
/// </summary>
internal static class StackWrites
{
    /// <summary>
    /// Returns the registers among A, X and Y that hold the stack pointer after an instruction
    /// runs as <paramref name="mnemonic"/>, where <paramref name="before"/> held it before. A
    /// <c>tsx</c> or a <c>tsc</c> copies it, a transfer passes it on, and anything else that
    /// writes a register, or a call, leaves that register holding something else.
    /// </summary>
    public static Registers Pointing(MnemonicKind mnemonic, AddressingMode? mode, long? immediate, Registers before)
    {
        if (Instructions.IsCall(mnemonic) || mnemonic is MnemonicKind.Brk or MnemonicKind.Cop)
            return Registers.None;
        if (mnemonic == MnemonicKind.Tsx)
            return before | Registers.X;
        if (mnemonic == MnemonicKind.Tsc)
            return before | Registers.A;
        if (RegisterEffects.Moved(mnemonic) is { } moved)
            return (before & moved.From) != Registers.None ? before | moved.To : before & ~moved.To;
        return before & ~RegisterEffects.Written(mnemonic, mode, immediate);
    }

    /// <summary>
    /// Returns whether the store at <paramref name="step"/>, laid out in <paramref name="mode"/>,
    /// writes into the bytes on the stack, where <paramref name="pointing"/> holds the registers
    /// that hold S there.
    /// </summary>
    public static bool Into(SemanticModel model, Step step, AddressingMode? mode, Registers pointing) =>
        Into(mode, StepOperands.Constant(model, step), mode == AddressingMode.StackRelative && IntoFrame(model, step), pointing);

    /// <summary>
    /// Returns whether <paramref name="decoded"/>, a store the bytes of a <see cref="HiddenPath"/>
    /// decode as, writes into the bytes on the stack, where <paramref name="pointing"/> holds the
    /// registers that hold S there. A decoded store names no member of a <c>.frame</c>, because it
    /// has no operand as written.
    /// </summary>
    public static bool Into(HiddenInstruction decoded, Registers pointing) =>
        Into(decoded.Mode, decoded.Operand, intoFrame: false, pointing);

    /// <summary>
    /// Returns whether a store laid out in <paramref name="mode"/> writes into the bytes on the
    /// stack. <paramref name="address"/> is the value of its operand, or null where nt65 cannot
    /// work it out, and <paramref name="intoFrame"/> tells whether the operand names a member of a
    /// <c>.frame</c>.
    /// </summary>
    private static bool Into(AddressingMode? mode, long? address, bool intoFrame, Registers pointing)
    {
        var onPage = address is not { } known ? (bool?)null : (known & ~0xffL) == 0x100;
        return mode switch
        {
            AddressingMode.StackRelative => !intoFrame,
            AddressingMode.AbsoluteX => (pointing & Registers.X) != Registers.None && onPage != false,
            AddressingMode.AbsoluteY => (pointing & Registers.Y) != Registers.None && onPage != false,
            AddressingMode.Absolute => onPage == true,
            _ => false,
        };
    }

    /// <summary>
    /// Returns whether the operand at <paramref name="step"/> names a member of a <c>.frame</c>.
    /// The frame's bytes are ones the routine laid out for itself, so a store to a member changes
    /// no saved value.
    /// </summary>
    private static bool IntoFrame(SemanticModel model, Step step) =>
        (step.Statement as InstructionStatementSyntax)?.Operand is { } operand
        && operand.DescendantNodes().OfType<NameExpressionSyntax>().Any(name =>
            name.GlobalToken is null && name.Names is [var first, ..] && model.SymbolAt(first) is { Kind: SymbolKind.Frame });
}
