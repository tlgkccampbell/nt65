using Norristown.Processor;
using Norristown.Project;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Layout;

/// <summary>
/// Which addressing mode each instruction is laid out in, and what that makes wrong with the
/// operand it was written with.
/// <para>
/// The shape of the operand says which modes it could possibly be, the CPU's table says which
/// of those the mnemonic has, and how wide an address the operand reaches says which of those
/// to take: the narrowest that reaches. A prefix written in the source wins over all of it.
/// </para>
/// </summary>
public sealed partial class CodeLayout
{
    /// <summary>The modes a plain address can be: sized by its width, or a branch target.</summary>
    private static AddressingMode[] Unindexed =>
    [
        AddressingMode.Direct, AddressingMode.Absolute, AddressingMode.Long,
        AddressingMode.Relative, AddressingMode.RelativeLong,
    ];

    /// <summary>Whether an operand is written <c>d:</c>, reaching a constant address through the direct page.</summary>
    public static bool ThroughDirectPage(SyntaxNode operand) =>
        operand is AbsoluteOperandSyntax { Prefix: { } prefix } && char.ToLowerInvariant(prefix.Name.Text[0]) == 'd';

    /// <summary>
    /// The expression an operand addresses, which is what an address size is worked out from.
    /// An <c>operand</c> argument written without braces is an expression, and so is the whole
    /// of what it addresses.
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
    /// The modes an operand's shape could possibly be, before the mnemonic and the CPU have
    /// their say. A shape that nothing on this CPU has, such as a long operand, yields none.
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

                // A second expression rather than an index register: the branch target of
                // `bbr0 flags, @skip`.
                return absolute.Second is not null ? [AddressingMode.DirectRelative] : Unindexed;

            // An `operand` argument written without braces is an expression, and a plain
            // address operand by being one.
            default:
                return Unindexed;
        }
    }

    /// <summary>Whether the operand ends in <c>,x</c>, <c>,y</c> or <c>,s</c>.</summary>
    private static bool IndexedBy(SyntaxNode operand, string register)
    {
        foreach (var token in operand.ChildTokens)
        {
            if (token.Kind == SyntaxKind.Register && token.Text.Equals(register, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// The address-size prefix written in the operand, which wins over everything. <c>d:</c>
    /// makes a direct operand of a constant address, reached through the direct page.
    /// </summary>
    private static AddressSize? WrittenPrefix(SyntaxNode operand)
    {
        if (operand is not AbsoluteOperandSyntax { Prefix: { } prefix })
            return null;
        return char.ToLowerInvariant(prefix.Name.Text[0]) switch
        {
            'z' or 'd' => AddressSize.ZeroPage,
            'a' => AddressSize.Absolute,
            'f' => AddressSize.Far,
            _ => null,
        };
    }

    private static string Spell(AddressSize size) => size switch
    {
        AddressSize.ZeroPage => "direct-page",
        AddressSize.Absolute => "absolute",
        _ => "far",
    };

    /// <summary>
    /// Which of the candidate modes the operand's own width calls for.
    /// <paramref name="sizeUnknown"/> says the immediate is sized by a register whose width the
    /// analysis does not know, which it has already reported.
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
            // A prefix wins, so one the only form cannot honour is an error rather than a
            // prefix quietly dropped: `lda z:($10),y` has no direct form, and ca65 would read
            // the text as `(dp),y`. `d:` says what is wrong with it where its offset is worked out.
            if (WrittenPrefix(operand) is { } written && !ThroughDirectPage(operand)
                && Instructions.Width(candidates[0]) is { } width && width != written
                && !Instructions.IsControlTransfer(mnemonic.Text))
            {
                Report(operand, Catalogue.AddressingModeMissing.Says(
                    mnemonic.Text, Spell(written), CpuNames.Spell(cpu)));
            }

            // The one form there is reaches an address of its width and no wider: `(ptr),y`
            // takes a zero-page pointer, and an absolute one would be cut to its low byte by
            // the linker, if it noticed at all. A control transfer's target is checked for
            // distance instead.
            else if (WrittenPrefix(operand) is null
                && !(Instructions.IsControlTransfer(mnemonic.Text) && candidates[0] is AddressingMode.Absolute
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

        var required = WrittenPrefix(operand) ?? (Expression(operand) is { } expression
            ? model.AddressSizeOf(expression, segment, expansion)
            : null);

        // Where nothing says how wide it is, the reason has already been reported; the widest
        // form always reaches, so take that rather than say so twice.
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
    /// What the operand itself must satisfy: a control transfer takes a near target and is
    /// not sized by a prefix, and an immediate fits the byte or two it is given.
    /// </summary>
    private void CheckOperand(
        SyntaxToken mnemonic, SyntaxNode operand, AddressingMode mode, OperandSubstitution? substituted,
        int? bits, bool sizeUnknown)
    {
        if (Expression(operand) is not { } expression)
            return;

        // Text is what data is written from. An operand is a number or an address.
        if (model.ValueOf(expression, expansion, SpanOf, CyclesOf).IsString)
        {
            Report(expression, Catalogue.OperandIsText.Says(expression.GetText().Trim()));
            return;
        }

        if (mode is AddressingMode.Relative or AddressingMode.RelativeLong or AddressingMode.Absolute
                or AddressingMode.AbsoluteIndirect or AddressingMode.AbsoluteIndirectX or AddressingMode.Long
                or AddressingMode.AbsoluteIndirectLong
            && Instructions.IsControlTransfer(mnemonic.Text))
        {
            if (WrittenPrefix(operand) is not null)
            {
                Report(operand,
                    Catalogue.TransferPrefix.Says(mnemonic.Text));
            }

            // On the 65816 whether a routine is called near or far is its signature's to say,
            // and the processor-state analysis checks it where it checks the rest of the call.
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
    /// That a control transfer reaches as far as its target is: <c>jsr</c>, <c>jmp</c> and
    /// the branches a near one, <c>jsl</c> and <c>jml</c> a far one.
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
        var near = mnemonic.Text.Equals("jsl", StringComparison.OrdinalIgnoreCase) ? "jsr" : "jmp";
        Report(expression, Catalogue.TargetTooNear.Says(mnemonic.Text, Spell(size.Value), near));
    }

    /// <summary>
    /// <c>d:</c> on a constant address: the offset into the direct page it is written as, once
    /// the analysis knows D here. What is wrong with D is the analysis's to report; what is
    /// wrong with the operand itself is reported here.
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
    /// On the 65816 a symbol in a segment reached through a direct page other than 0 means
    /// something only as a direct operand, D plus its offset. As an absolute or long operand it
    /// reaches the offset in bank B instead, whatever made the operand that wide. <c>pea</c> and
    /// <c>per</c> reach no memory, so the offset is all they push.
    /// </summary>
    private void CheckDirectPageSymbols(SyntaxToken mnemonic, SyntaxNode operand, AddressingMode mode)
    {
        if (Instructions.Width(mode) is not (AddressSize.Absolute or AddressSize.Far)
            || mnemonic.Text.ToLowerInvariant() is "pea" or "per" || Expression(operand) is not { } expression)
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

    /// <summary>Whether an expression names a routine, which carries a signature.</summary>
    private bool NamesRoutine(ExpressionSyntax expression) =>
        Targets.Of(model, expression, expansion) is { Symbol.Signature: not null };

    /// <summary>
    /// Whether the argument's mode has the next byte the body asked for. An immediate, the
    /// accumulator, an indirect operand and a stack-relative one have no second byte to
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

        // The pair is what is wrong — this body line with this argument — so it is reported
        // at the call, which is the side that can change it, and the body line is named.
        var what = given.ByteOf ? "`.byteof`" : $"`{given.Parameter.Name} + n`";
        ReportPaired(given.At, Catalogue.OperandHasNoNextByte.Says(what, given.Parameter.Name, given.Mode));
    }

    /// <summary>
    /// Something that is only wrong for these arguments: reported at the call, which is the
    /// side that can change them, with the body line that wrote it named beside it.
    /// </summary>
    private void ReportPaired(SyntaxNode inTheBody, DiagnosticMessage message)
    {
        if (expansion?.NearestCall is not { } call)
            return;
        diagnostics.Add(new Diagnostic(call.Tree.GetSpan(call.Span), Severity.Error, message,
            [new RelatedSpan(inTheBody.Tree.GetSpan(inTheBody.Span), "in the macro body")]));
    }
}
