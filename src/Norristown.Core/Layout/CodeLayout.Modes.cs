using Norristown.Processor;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Layout;

/// <summary>
/// Chooses the addressing mode each instruction is laid out in, and reports the problems that
/// choice reveals in the instruction's operand.
/// <para>
/// The shape of the operand gives the modes it could possibly be, and the CPU's table narrows
/// those to the ones the mnemonic has. The operand's address size then picks among them the
/// narrowest mode wide enough for it. A size prefix in the source overrides all of this.
/// </para>
/// </summary>
public sealed partial class CodeLayout
{
    /// <summary>
    /// Gets the modes a plain address can take, which are the modes sized by the address's width
    /// and the branch-target modes.
    /// </summary>
    private static AddressingMode[] Unindexed =>
    [
        AddressingMode.Direct, AddressingMode.Absolute, AddressingMode.Long,
        AddressingMode.Relative, AddressingMode.RelativeLong,
    ];

    /// <summary>
    /// Returns whether an operand has a <c>d:</c> prefix, which reaches a constant address through
    /// the direct page.
    /// </summary>
    public static bool ThroughDirectPage(SyntaxNode operand) =>
        operand is AbsoluteOperandSyntax { Prefix: { } prefix } && char.ToLowerInvariant(prefix.Name.Text[0]) == 'd';

    /// <summary>
    /// Returns the expression an operand addresses, from which an address size is worked out. An
    /// <c>operand</c> argument passed without braces is itself an expression, and the whole of it
    /// is the address.
    /// </summary>
    public static ExpressionSyntax? Expression(SyntaxNode operand) => operand switch
    {
        AbsoluteOperandSyntax absolute => absolute.Address,
        ImmediateOperandSyntax immediate => immediate.Value,
        IndirectOperandSyntax indirect => indirect.Address,
        IndexedIndirectOperandSyntax indexed => indexed.Address,
        LongIndirectOperandSyntax indirect => indirect.Address,
        _ => operand as ExpressionSyntax,
    };

    /// <summary>
    /// Returns the modes an operand's shape could possibly be, before checking which of them the
    /// mnemonic has on the target CPU. A shape that matches no addressing mode yields none.
    /// </summary>
    private static AddressingMode[] Plausible(SyntaxNode? operand)
    {
        if (operand is null)
            return [AddressingMode.Implied, AddressingMode.Accumulator];

        switch (operand)
        {
            case AccumulatorOperandSyntax:
                return [AddressingMode.Accumulator];

            // Two immediates are the source and destination banks of `mvn` and `mvp`.
            case ImmediateOperandSyntax immediate:
                return immediate.SecondValue is not null ? [AddressingMode.BlockMove] : [AddressingMode.Immediate];
            case IndirectOperandSyntax:
                return IndexedBy(operand, "y")
                    ? [AddressingMode.DirectIndirectY]
                    : [AddressingMode.DirectIndirect, AddressingMode.AbsoluteIndirect];
            case IndexedIndirectOperandSyntax:
                if (IndexedBy(operand, "s"))
                    return IndexedBy(operand, "y") ? [AddressingMode.StackRelativeIndirectY] : [];
                return IndexedBy(operand, "x")
                    ? [AddressingMode.DirectIndirectX, AddressingMode.AbsoluteIndirectX]
                    : [];
            case LongIndirectOperandSyntax:
                return IndexedBy(operand, "y")
                    ? [AddressingMode.DirectIndirectLongY]
                    : [AddressingMode.DirectIndirectLong, AddressingMode.AbsoluteIndirectLong];
            case AbsoluteOperandSyntax absolute:
                if (IndexedBy(operand, "x"))
                    return [AddressingMode.DirectX, AddressingMode.AbsoluteX, AddressingMode.LongX];
                if (IndexedBy(operand, "y"))
                    return [AddressingMode.DirectY, AddressingMode.AbsoluteY];
                if (IndexedBy(operand, "s"))
                    return [AddressingMode.StackRelative];

                // A second expression rather than an index register is the branch target of
                // `bbr0 flags, @skip`.
                return absolute.Second is not null ? [AddressingMode.DirectRelative] : Unindexed;

            // An `operand` argument passed without braces is an expression, and is treated as a
            // plain address operand.
            default:
                return Unindexed;
        }
    }

    /// <summary>
    /// Returns whether the operand is indexed by <paramref name="register"/>, as in <c>,x</c>,
    /// <c>,y</c> or <c>,s</c>.
    /// </summary>
    private static bool IndexedBy(SyntaxNode operand, string register)
    {
        foreach (var token in operand.ChildTokens)
        {
            if (token.Kind == SyntaxKind.Register && token.Text.Equals(register, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string Spell(AddressSize size) => size switch
    {
        AddressSize.ZeroPage => "direct-page",
        AddressSize.Absolute => "absolute",
        _ => "far",
    };

    /// <summary>
    /// Returns the candidate mode that the operand's own width calls for.
    /// <paramref name="sizeUnknown"/> indicates that the immediate is sized by a register whose
    /// width the analysis does not know, which the analysis has already reported.
    /// </summary>
    private AddressingMode Choose(
        SyntaxToken mnemonic, SyntaxNode? operand, AddressingMode[] candidates,
        OperandSubstitution? substituted, int? bits, bool sizeUnknown)
    {
        var widths = candidates.OrderBy(Instructions.Length).ToArray();
        if (operand is null)
            return widths[0];
        if (candidates.Length == 1)
        {
            // A prefix in the source is binding, so a prefix that the only available form cannot
            // honour is an error rather than being quietly dropped. For example, `lda z:($10),y`
            // has no direct form, and ca65 would read the text as `(dp),y`. A `d:` prefix is
            // checked separately, where its direct-page offset is worked out.
            if (Operands.WrittenPrefix(operand) is { } written && !ThroughDirectPage(operand)
                && Instructions.Width(candidates[0]) is { } width && width != written
                && !Instructions.IsControlTransfer(mnemonic.MnemonicKind))
            {
                Report(operand, Catalogue.AddressingModeMissing.Says(
                    mnemonic.Text, Spell(written), CpuNames.Spell(cpu)));
            }

            // The only available form reaches an address of its own width and no wider. For
            // example, `(ptr),y` takes a zero-page pointer, and the linker would cut an absolute
            // pointer to its low byte, if it noticed at all. A control transfer's target is
            // checked for distance instead.
            else if (Operands.WrittenPrefix(operand) is null
                && !(Instructions.IsControlTransfer(mnemonic.MnemonicKind) && candidates[0] is AddressingMode.Absolute
                    or AddressingMode.Long or AddressingMode.Relative or AddressingMode.RelativeLong
                    or AddressingMode.DirectRelative)
                && Instructions.Width(candidates[0]) is { } reach
                && Expression(operand) is { } pointer
                && model.AddressSizeOf(pointer, segment, expansion) is { } wide && wide > reach)
            {
                Report(operand, Catalogue.AddressingModeTooNarrow.Says(
                    mnemonic.Text, Spell(reach), pointer.GetText().Trim(), Spell(wide)));
            }
            CheckOperand(mnemonic, operand, candidates[0], substituted, bits, sizeUnknown);
            return candidates[0];
        }

        var required = Operands.WrittenPrefix(operand) ?? (Expression(operand) is { } expression
            ? model.AddressSizeOf(expression, segment, expansion)
            : null);

        // Where the operand's width is unknown, the reason has already been reported. The widest
        // form always reaches, so it is taken rather than reporting the problem twice.
        var chosen = required is null
            ? widths[^1]
            : widths.FirstOrDefault(mode => Instructions.Width(mode) >= required, widths[^1]);
        if (required is { } size && Instructions.Width(chosen) < size)
        {
            Report(operand,
                Catalogue.AddressSizeUnreachable.Says(mnemonic.Text, Spell(size), CpuNames.Spell(cpu)));
        }
        CheckOperand(mnemonic, operand, chosen, substituted, bits, sizeUnknown);
        return chosen;
    }

    /// <summary>
    /// Checks the operand itself. A control transfer takes a near target and is not sized by a
    /// prefix, and an immediate must fit the byte or two it is given.
    /// </summary>
    private void CheckOperand(
        SyntaxToken mnemonic, SyntaxNode operand, AddressingMode mode, OperandSubstitution? substituted,
        int? bits, bool sizeUnknown)
    {
        if (Expression(operand) is not { } expression)
            return;

        // Text can only be emitted as data. An operand is a number or an address.
        if (model.ValueOf(expression, expansion, SpanOf, CyclesOf).IsString)
        {
            Report(expression, Catalogue.OperandIsText.Says(expression.GetText().Trim()));
            return;
        }

        if (mode is AddressingMode.Relative or AddressingMode.RelativeLong or AddressingMode.Absolute
                or AddressingMode.AbsoluteIndirect or AddressingMode.AbsoluteIndirectX or AddressingMode.Long
                or AddressingMode.AbsoluteIndirectLong
            && Instructions.IsControlTransfer(mnemonic.MnemonicKind))
        {
            if (Operands.WrittenPrefix(operand) is not null)
            {
                Report(operand,
                    Catalogue.TransferPrefix.Says(mnemonic.Text));
            }

            // On the 65816 whether a routine is called near or far is decided by its signature,
            // and the processor-state analysis checks that along with the rest of the call.
            else if (!(cpu == Cpu.Wdc65816 && NamesRoutine(expression)))
            {
                CheckDistance(mnemonic, expression, mode);
            }
            return;
        }

        // `.byteof` takes one byte of the value, so the value it is taken from is not the
        // one that has to fit.
        if (mode != AddressingMode.Immediate || substituted is { ByteOf: true })
            return;
        if (model.ValueOf(expression, expansion, SpanOf, CyclesOf).AsNumber() is not { } value)
        {
            if (!sizeUnknown && DataLengths.TooWide(expression, bits == 16 ? 2 : 1,
                bits == 16 ? "this immediate is two bytes" : "an immediate is one byte", model, expansion) is { } wide)
            {
                Report(expression, wide);
            }
            return;
        }
        // An immediate is a byte or a word slot, which takes a signed value as its two's complement.
        var (low, high) = DataLengths.Holds(bits == 16 ? ".word" : ".byte")!.Value;
        if (sizeUnknown && value is >= -0x8000 and <= 0xffff)
            return;
        if (value < low || value > high)
        {
            Report(expression, Catalogue.ImmediateTooWide.Says(
                bits == 16 ? "this immediate is two bytes" : "an immediate is one byte", Value.Of(value)));
        }
    }

    /// <summary>
    /// Checks that a control transfer's reach matches its target: <c>jsr</c>, <c>jmp</c> and
    /// the branches need a near target, and <c>jsl</c> and <c>jml</c> a far one.
    /// </summary>
    private void CheckDistance(SyntaxToken mnemonic, ExpressionSyntax expression, AddressingMode mode)
    {
        var size = model.AddressSizeOf(expression, segment, expansion);
        if (mode != AddressingMode.Long)
        {
            if (size == AddressSize.Far)
                Report(expression, Catalogue.TargetTooFar.Says(mnemonic.Text));
            return;
        }

        // A constant address is taken as written: `jml $008000` leaves the current bank for
        // bank 0, which is what a long jump to a small number is for.
        if (size is null or AddressSize.Far || model.ValueOf(expression, expansion, SpanOf, CyclesOf).AsNumber() is not null)
            return;
        var near = SyntaxFacts.TextOf(mnemonic.MnemonicKind == MnemonicKind.Jsl ? MnemonicKind.Jsr : MnemonicKind.Jmp);
        Report(expression, Catalogue.TargetTooNear.Says(mnemonic.Text, Spell(size.Value), near));
    }

    /// <summary>
    /// Returns the offset into the direct page that the instruction encodes for <c>d:</c> on a
    /// constant address, when the analysis knows D at this point. Problems with D itself are
    /// reported by the analysis, and problems with the operand are reported here.
    /// </summary>
    private long? DirectOffset(SyntaxToken mnemonic, SyntaxNode operand, AddressingMode mode, ProcessorState? state)
    {
        if (Expression(operand) is not { } expression)
            return null;
        if (cpu != Cpu.Wdc65816)
        {
            Report(operand, Catalogue.DirectPageNeeds65816.Says(CpuNames.Spell(cpu)));
            return null;
        }
        if (model.ValueOf(expression, expansion, SpanOf, CyclesOf).AsNumber() is not { } address)
        {
            Report(operand, Catalogue.DirectPagePrefixOnSymbol);
            return null;
        }
        if (Instructions.Width(mode) != AddressSize.ZeroPage)
        {
            Report(operand, Catalogue.DirectPageFormMissing.Says(mnemonic.Text));
            return null;
        }
        return state?.D is { IsKnown: true } page && address >= page.Value && address <= page.Value + 0xff
            ? address - page.Value
            : null;
    }

    /// <summary>
    /// Reports each symbol in a segment addressed through a nonzero direct page that is used as
    /// an absolute or long operand on the 65816. Such a symbol is only meaningful as a direct
    /// operand, where it means D plus its offset. As an absolute or long operand it would instead
    /// address the offset in bank B, regardless of what made the operand that wide. <c>pea</c>
    /// and <c>per</c> access no memory and just push the offset, so they are exempt.
    /// </summary>
    private void CheckDirectPageSymbols(SyntaxToken mnemonic, SyntaxNode operand, AddressingMode mode)
    {
        if (Instructions.Width(mode) is not (AddressSize.Absolute or AddressSize.Far)
            || mnemonic.MnemonicKind is MnemonicKind.Pea or MnemonicKind.Per || Expression(operand) is not { } expression)
        {
            return;
        }
        foreach (var symbol in AddressSymbols.In(model, expression, expansion))
        {
            if (symbol.Segment is not { } name || model.Segments.Find(name) is not { DirectPage: { } page and not 0 } segment)
                continue;
            Report(expression, Catalogue.DirectPageOnly.Says(
                symbol.DisplayName,
                segment.Name,
                StateValue.Hex(page, 4),
                (mode == AddressingMode.Long || mode == AddressingMode.LongX ? "a long" : "an absolute")));
        }
    }

    /// <summary>Returns whether an expression names a routine, meaning a symbol that carries a signature.</summary>
    private bool NamesRoutine(ExpressionSyntax expression) =>
        Targets.Of(model, expression, expansion) is { Symbol.Signature: not null };

    /// <summary>
    /// Checks that the argument's mode has the next byte the body asked for. An immediate, the
    /// accumulator, an indirect operand and a stack-relative operand have no second byte to
    /// name, and <c>.byteof</c> shifts an immediate rather than adding to it.
    /// </summary>
    private void CheckSubstitution(OperandSubstitution? substituted)
    {
        if (substituted is not { } given || given.HasNextByte)
            return;
        if (given.ByteOf && given.Operand is ImmediateOperandSyntax)
            return;
        if (!given.ByteOf && given.Offset == 0)
            return;

        // The problem lies in pairing this body line with this argument, so it is reported at
        // the call, which is the side that can change it, and the body line is named.
        var what = given.ByteOf ? "`.byteof`" : $"`{given.Parameter.Name} + n`";
        ReportPaired(given.At, Catalogue.OperandHasNoNextByte.Says(what, given.Parameter.Name, given.Mode));
    }

    /// <summary>
    /// Reports a problem that arises only with these arguments. It is reported at the call,
    /// which is the side that can change them, with the body line that contains the problem
    /// named beside it.
    /// </summary>
    private void ReportPaired(SyntaxNode inTheBody, DiagnosticMessage message)
    {
        if (expansion?.NearestCall is not { } call)
            return;
        diagnostics.Add(new Diagnostic(call.Tree.GetSpan(call.Span), Severity.Error, message,
            [new RelatedSpan(inTheBody.Tree.GetSpan(inTheBody.Span), "in the macro body")]));
    }
}
