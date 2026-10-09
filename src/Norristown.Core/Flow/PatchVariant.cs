using Norristown.Layout;
using Norristown.Processor;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// Represents one instruction a <c>.patch … as</c> says a store can turn the patched instruction
/// into. The store writes the opcode, so it has to be one known to start at the instruction's
/// first byte. The variant takes the patched instruction's addressing mode and operand. The
/// analyses take the union of what the written instruction and each variant do, so a variant
/// must change nothing they cannot join: it may not move the stack, change the processor's widths
/// or run a handler, and it goes where the written one goes, both running on or both branching.
/// </summary>
/// <param name="Name">The mnemonic as the <c>.patch</c> names it.</param>
/// <param name="On">The expansion the <c>.patch</c> is in, or null outside every expansion.</param>
/// <param name="Written">The step of the patched instruction.</param>
/// <param name="WrittenMnemonic">The patched instruction's mnemonic, as it is written.</param>
/// <param name="Mode">The patched instruction's addressing mode, or null where layout gave it none.</param>
/// <param name="Cpu">The CPU the program is built for.</param>
/// <param name="AtOpcode">
/// Whether the store the <c>.patch</c> follows is known to write from the patched instruction's
/// first byte, its opcode.
/// </param>
internal sealed record PatchVariant(
    NameExpressionSyntax Name, Expansion? On, Step Written, MnemonicKind WrittenMnemonic, AddressingMode? Mode, Cpu Cpu,
    bool AtOpcode)
{
    /// <summary>Gets the variant's mnemonic, or <see cref="MnemonicKind.None"/> where the name is not one.</summary>
    public MnemonicKind Mnemonic => Name is { Names.Length: 1, GlobalToken: null, SimpleName: { Kind: SyntaxKind.Mnemonic } token }
        ? token.MnemonicKind
        : MnemonicKind.None;

    /// <summary>
    /// Gets why the variant cannot stand in for the written instruction, as a message completes
    /// <c>.patch cannot list `x` for `y`: …</c>, or null when it can.
    /// </summary>
    public string? Problem
    {
        get
        {
            var mnemonic = Mnemonic;
            var name = SyntaxFacts.TextOf(mnemonic);
            var written = SyntaxFacts.TextOf(WrittenMnemonic);
            if (mnemonic == MnemonicKind.None)
                return "it is not a mnemonic";
            if (!AtOpcode)
                return "a variant replaces the opcode, and the store is not known to write it";
            if (!Instructions.Has(Cpu, mnemonic))
                return $"the {CpuNames.Format(Cpu)} has no `{name}`";
            if (Mode is not { } mode || !Instructions.Modes(Cpu, mnemonic).Contains(mode))
                return $"the {CpuNames.Format(Cpu)} has no `{name}` in the form `{written}` is written in";
            if (mode == AddressingMode.Immediate && Instructions.SizedBy(mnemonic) != Instructions.SizedBy(WrittenMnemonic))
                return $"its immediate is not sized as the immediate of `{written}` is";
            var control = Instructions.Facts(mnemonic).Control;
            var writtenControl = Instructions.Facts(WrittenMnemonic).Control;
            if (control != writtenControl || control is not (Control.Through or Control.Branches))
                return $"a variant has to run on where `{written}` runs on, and branch where it branches";
            if (Moves(mnemonic))
                return $"`{name}` moves the stack, changes the processor's widths or runs a handler";
            if (Moves(WrittenMnemonic))
                return $"`{written}` moves the stack, changes the processor's widths or runs a handler";
            return null;
        }
    }

    /// <summary>
    /// Returns a value indicating whether <paramref name="mnemonic"/> moves the stack, changes the
    /// processor's widths or runs a handler, none of which the analyses can take the union of.
    /// </summary>
    private static bool Moves(MnemonicKind mnemonic) =>
        Instructions.Facts(mnemonic) is { Pushes: not null } or { Pulls: not null }
        || RegisterEffects.SetsStackPointer(mnemonic)
        || mnemonic is MnemonicKind.Rep or MnemonicKind.Sep or MnemonicKind.Xce or MnemonicKind.Brk or MnemonicKind.Cop
            or MnemonicKind.Rti or MnemonicKind.Wai;
}
