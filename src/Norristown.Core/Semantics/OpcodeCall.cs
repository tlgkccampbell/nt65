using Norristown.Processor;
using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Evaluates <c>.opcode(mnemonic)</c> and <c>.opcode(mnemonic, mode)</c>, the byte an instruction
/// form is written as on the build's CPU. A mode is named with the words <c>.mode</c> returns,
/// with <c>zp</c>, <c>zpx</c> and <c>zpy</c> for the direct page and <c>far</c> and <c>farx</c>
/// for the long addresses. The mode may be left out where the instruction has one form, or has a
/// form with no operand or with <c>a</c> as its operand, which is the form it then names.
/// </summary>
internal static class OpcodeCall
{
    /// <summary>Gets the words that name a mode, in the order a message lists them.</summary>
    public static IReadOnlyList<string> ModeWords { get; } =
        ["imm", "acc", "zp", "zpx", "zpy", "abs", "absx", "absy", "ind", "indx", "indy", "sr", "sry", "long", "longy", "far", "farx"];

    /// <summary>
    /// Returns the word that names <paramref name="mode"/>, or null for a mode that is an
    /// instruction's only form wherever it appears, such as a branch's.
    /// </summary>
    public static string? WordOf(AddressingMode mode) => mode switch
    {
        AddressingMode.Immediate => "imm",
        AddressingMode.Accumulator => "acc",
        AddressingMode.Direct => "zp",
        AddressingMode.DirectX => "zpx",
        AddressingMode.DirectY => "zpy",
        AddressingMode.Absolute => "abs",
        AddressingMode.AbsoluteX => "absx",
        AddressingMode.AbsoluteY => "absy",
        AddressingMode.DirectIndirect or AddressingMode.AbsoluteIndirect => "ind",
        AddressingMode.DirectIndirectX or AddressingMode.AbsoluteIndirectX => "indx",
        AddressingMode.DirectIndirectY => "indy",
        AddressingMode.StackRelative => "sr",
        AddressingMode.StackRelativeIndirectY => "sry",
        AddressingMode.DirectIndirectLong or AddressingMode.AbsoluteIndirectLong => "long",
        AddressingMode.DirectIndirectLongY => "longy",
        AddressingMode.Long => "far",
        AddressingMode.LongX => "farx",
        _ => null,
    };

    /// <summary>
    /// Returns the value of a call to <c>.opcode</c> with <paramref name="given"/> as its
    /// arguments, for a build for <paramref name="cpu"/>. The caller has checked that there are one
    /// or two. Problems with the arguments are reported through <paramref name="report"/>.
    /// </summary>
    public static Value Evaluate(IReadOnlyList<SyntaxNode> given, Cpu cpu, Action<DiagnosticMessage> report)
    {
        var word = given.Count == 2 ? WordIn(given[1]) : null;
        if (given[0] is not NameExpressionSyntax { Names.Length: 1, SimpleName: { Kind: SyntaxKind.Mnemonic } mnemonic }
            || (given.Count == 2 && word is null))
        {
            report(Catalogue.OpcodeArgument);
            return Value.Unknown;
        }

        var name = mnemonic.Text.ToLowerInvariant();
        var written = word is null ? name : $"{name}, {word}";
        var forms = Instructions.Modes(cpu, mnemonic.MnemonicKind);
        if (forms.Count == 0)
        {
            report(Catalogue.OpcodeForm.Message(written, CpuNames.Format(cpu), SyntaxFacts.IsLongBranch(mnemonic.MnemonicKind)
                ? $"`{name}` is written as one instruction or two, as the target's reach decides"
                : $"it has no `{name}`"));
            return Value.Unknown;
        }

        var words = ModeWords.Where(mode => forms.Any(form => WordOf(form) == mode)).ToList();
        var listed = string.Join(", ", words.Select(mode => $"`{mode}`"));
        AddressingMode? chosen = word is null ? Bare(forms) : forms.Where(form => WordOf(form) == word).Cast<AddressingMode?>().FirstOrDefault();
        if (chosen is not { } mode)
        {
            report(Catalogue.OpcodeForm.Message(written, CpuNames.Format(cpu),
                word is null ? $"`{name}` has several forms, so name one of {listed}"
                : words.Count == 0 ? $"`{name}` takes no mode"
                : $"`{name}` has no `{word}` form, only {listed}"));
            return Value.Unknown;
        }
        return Value.Of((long)Opcodes.Encode(cpu, mnemonic.MnemonicKind, mode)!.Value);
    }

    /// <summary>
    /// Returns the form a call that names no mode means: the form with no operand, the form with
    /// <c>a</c>, or the only form. Returns null when the instruction has several and none of the
    /// first two.
    /// </summary>
    private static AddressingMode? Bare(IReadOnlySet<AddressingMode> forms) =>
        forms.Contains(AddressingMode.Implied) ? AddressingMode.Implied
        : forms.Contains(AddressingMode.Accumulator) ? AddressingMode.Accumulator
        : forms.Count == 1 ? forms.Single()
        : null;

    /// <summary>Returns the mode word an argument is, in lower case, or null when it is not one.</summary>
    private static string? WordIn(SyntaxNode argument) =>
        argument is NameExpressionSyntax { Names.Length: 1, GlobalToken: null, SimpleName: { } token }
            && ModeWords.FirstOrDefault(mode => mode.Equals(token.Text, StringComparison.OrdinalIgnoreCase)) is { } word
            ? word
            : null;
}
