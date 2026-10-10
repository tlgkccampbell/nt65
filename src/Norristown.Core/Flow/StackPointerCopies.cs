using Norristown.Processor;
using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// Decides when a <c>txs</c> or <c>tcs</c> moves the stack pointer back to a copy that a
/// <c>tsx</c> or <c>tsc</c> took, so that every stack tracker treats it the same way. A copy is
/// one the trackers can rely on while only pushes have happened since it was taken, and moving
/// back to it then drops exactly those pushes. Any other way of setting the stack pointer leaves
/// the stack unknown.
/// <para>
/// Each tracker keeps its own stack, and the registers that hold the stack pointer as
/// <see cref="StackWrites.Pointing"/> finds them. Alongside those it keeps its stack as it was
/// where the copy was taken, which this class works out after each instruction and gives back at
/// the move.
/// </para>
/// </summary>
internal static class StackPointerCopies
{
    /// <summary>
    /// Returns the stack as it was where the registers that hold the stack pointer after an
    /// instruction copied it, or null where that cannot be relied on. A <c>tsx</c> or <c>tsc</c>
    /// copies the stack as it is. Any other instruction keeps the copy, unless it pulls, calls,
    /// returns, or on the 65816 may change the index width, which can clear the high byte of X.
    /// </summary>
    /// <typeparam name="TStack">The tracker's stack.</typeparam>
    /// <param name="mnemonic">The instruction.</param>
    /// <param name="cpu">The processor the instruction runs on.</param>
    /// <param name="wideIndex">
    /// A value indicating whether X is 16 bits wide before the instruction, or null where that is
    /// not known. On every processor but the 65816 it is not asked.
    /// </param>
    /// <param name="pointing">The registers that hold the stack pointer after the instruction.</param>
    /// <param name="pointed">The stack the copy held before the instruction, or null.</param>
    /// <param name="stack">The stack before the instruction, or null where it is not known.</param>
    /// <returns>The stack the copy was taken from, or null.</returns>
    public static TStack? Copied<TStack>(
        MnemonicKind mnemonic, Cpu cpu, bool? wideIndex, Registers pointing, TStack? pointed, TStack? stack)
        where TStack : class
    {
        if (pointing == Registers.None)
            return null;
        if (mnemonic is MnemonicKind.Tsx or MnemonicKind.Tsc)
        {
            // A copy taken earlier, at another depth, is still held in another register, and one
            // stack cannot describe both.
            var copied = mnemonic == MnemonicKind.Tsx ? Registers.X : Registers.A;
            if ((pointing & ~copied) != Registers.None && !Equals(pointed, stack))
                return null;

            // An 8-bit X holds only the low byte of the stack pointer on the 65816.
            return mnemonic == MnemonicKind.Tsx && cpu == Cpu.Wdc65816 && wideIndex != true ? null : stack;
        }
        if (Instructions.Facts(mnemonic).Pulls is not null || Instructions.IsCall(mnemonic)
            || mnemonic is MnemonicKind.Rts or MnemonicKind.Rtl or MnemonicKind.Rti or MnemonicKind.Brk or MnemonicKind.Cop
            || (cpu == Cpu.Wdc65816 && mnemonic is MnemonicKind.Sep or MnemonicKind.Rep or MnemonicKind.Xce))
        {
            return null;
        }
        return pointed;
    }

    /// <summary>
    /// Returns the stack after a <c>txs</c> or <c>tcs</c> moves the stack pointer back to a copy,
    /// or null where the instruction is not such a move and the stack is unknown after it. Only
    /// pushes may have happened since the copy, so moving back drops exactly those.
    /// </summary>
    /// <typeparam name="TStack">The tracker's stack.</typeparam>
    /// <param name="mnemonic">The instruction, which sets the stack pointer.</param>
    /// <param name="cpu">The processor the instruction runs on.</param>
    /// <param name="wideIndex">
    /// A value indicating whether X is 16 bits wide before the instruction, or null where that is
    /// not known. A <c>txs</c> from an 8-bit X on the 65816 sets only the low byte.
    /// </param>
    /// <param name="pointing">The registers that hold the stack pointer before the instruction.</param>
    /// <param name="pointed">The stack the copy held, or null.</param>
    /// <param name="stack">The stack before the instruction, or null where it is not known.</param>
    /// <param name="extends">
    /// Returns whether the stack, its first argument, is the copy's, its second, with only pushes
    /// on top.
    /// </param>
    /// <returns>The stack after the move, or null.</returns>
    public static TStack? MovedBack<TStack>(
        MnemonicKind mnemonic, Cpu cpu, bool? wideIndex, Registers pointing, TStack? pointed, TStack? stack,
        Func<TStack, TStack, bool> extends)
        where TStack : class
    {
        var from = mnemonic switch
        {
            MnemonicKind.Txs => Registers.X,
            MnemonicKind.Tcs => Registers.A,
            _ => Registers.None,
        };
        if ((pointing & from) == Registers.None || pointed is null || stack is null || !extends(stack, pointed))
            return null;
        return mnemonic == MnemonicKind.Txs && cpu == Cpu.Wdc65816 && wideIndex != true ? null : pointed;
    }
}
