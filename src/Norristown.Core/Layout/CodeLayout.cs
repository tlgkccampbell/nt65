using Norristown.Project;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Layout;

/// <summary>
/// What every line of a file assembles to: the addressing mode each instruction gets
///, how long each instruction and data directive is, and everything the CPU
/// makes wrong about them.
/// <para>
/// Syntax does not depend on the CPU, so every operand form parses everywhere; this is the
/// layer that says whether the target has it. Blocks belonging to a later stage are left
/// alone, as they are in binding.
/// </para>
/// </summary>
public sealed class CodeLayout
{
    private readonly SemanticModel model;
    private readonly Cpu cpu;
    private readonly Dictionary<int, LineLayout> lines = [];
    private readonly List<Diagnostic> diagnostics = [];
    private string segment = SegmentTable.DefaultSegment;

    private CodeLayout(SemanticModel model, Cpu cpu)
    {
        this.model = model;
        this.cpu = cpu;
    }

    /// <summary>The CPU this file was laid out for.</summary>
    public Cpu Cpu => cpu;

    /// <summary>What the CPU makes wrong, ordered by line and column.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; private set; } = [];

    /// <summary>Lays out <paramref name="model"/>'s file for <paramref name="cpu"/>.</summary>
    public static CodeLayout Create(SemanticModel model, Cpu cpu)
    {
        var layout = new CodeLayout(model, cpu);
        layout.WalkContainer(model.Tree.Root);
        layout.Diagnostics = Norristown.Diagnostics.Ordered(layout.diagnostics);
        return layout;
    }

    /// <summary>What a statement assembles to, or null when it generates no bytes.</summary>
    public LineLayout? Of(SyntaxNode statement) => lines.GetValueOrDefault(statement.Position);

    /// <summary>
    /// The modes an operand's shape could possibly be, before the mnemonic and the CPU have
    /// their say. A shape that nothing on this CPU has, such as a long operand, yields none.
    /// </summary>
    private static AddressingMode[] Plausible(SyntaxNode? operand)
    {
        if (operand is null)
            return [AddressingMode.Implied, AddressingMode.Accumulator];

        switch (operand.Kind)
        {
            case SyntaxKind.AccumulatorOperand:
                return [AddressingMode.Accumulator];
            case SyntaxKind.ImmediateOperand:
                return [AddressingMode.Immediate];
            case SyntaxKind.IndirectOperand:
                return IndexedBy(operand, "y")
                    ? [AddressingMode.DirectIndirectY]
                    : [AddressingMode.DirectIndirect, AddressingMode.AbsoluteIndirect];
            case SyntaxKind.IndexedIndirectOperand:
                return IndexedBy(operand, "x")
                    ? [AddressingMode.DirectIndirectX, AddressingMode.AbsoluteIndirectX]
                    : [];
            case SyntaxKind.AbsoluteOperand:
                if (IndexedBy(operand, "x"))
                    return [AddressingMode.DirectX, AddressingMode.AbsoluteX];
                if (IndexedBy(operand, "y"))
                    return [AddressingMode.DirectY, AddressingMode.AbsoluteY];
                if (IndexedBy(operand, "s"))
                    return [];

                // A second expression rather than an index register: the branch target of
                // `bbr0 flags, @skip`.
                return operand.ChildNodes.Count(c => c.Kind != SyntaxKind.AddressPrefix) > 1
                    ? [AddressingMode.DirectRelative]
                    : [AddressingMode.Direct, AddressingMode.Absolute, AddressingMode.Relative];
            default:
                return [];
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

    /// <summary>The address-size prefix written in the operand, which wins over everything.</summary>
    private static AddressSize? WrittenPrefix(SyntaxNode operand)
    {
        var prefix = operand.ChildNodes.FirstOrDefault(c => c.Kind == SyntaxKind.AddressPrefix);
        if (prefix is null || prefix.ChildTokens.Length == 0)
            return null;
        return char.ToLowerInvariant(prefix.ChildTokens[0].Text[0]) switch
        {
            'z' => AddressSize.ZeroPage,
            'a' => AddressSize.Absolute,
            'f' => AddressSize.Far,
            _ => null,
        };
    }

    /// <summary>The expression an operand addresses, which is what an address size is worked out from.</summary>
    private static SyntaxNode? Expression(SyntaxNode operand) =>
        operand.ChildNodes.FirstOrDefault(c => c.Kind != SyntaxKind.AddressPrefix);

    private void WalkContainer(SyntaxNode container)
    {
        foreach (var child in container.ChildNodes)
        {
            if (child.Green is GreenBlock block)
                WalkBlock(child, block.BlockKind);
            else if (child.Statement is { } statement)
                Statement(statement);
        }
    }

    private void WalkBlock(SyntaxNode block, BlockKind kind)
    {
        if (Constructs.IsDeferred(kind))
            return;

        if (kind == BlockKind.If && !model.Configuration.Includes(block))
            return;

        var lines = block.ChildNodes;
        var outer = segment;
        if (kind == BlockKind.Segment && lines.Length > 0 && lines[0].Statement is { } opener)
            segment = Constructs.SegmentOf(opener) ?? segment;
        else if (lines.Length > 0 && lines[0].Statement is { } other)
            Statement(other);

        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].Green is GreenBlock inner)
                WalkBlock(lines[i], inner.BlockKind);
            else if (lines[i].Statement is { } statement)
                Statement(statement);
        }
        segment = outer;
    }

    private void Statement(SyntaxNode statement)
    {
        switch (statement.Kind)
        {
            case SyntaxKind.InstructionStatement:
                Instruction(statement);
                break;
            case SyntaxKind.DataDirective:
                Data(statement);
                break;
            case SyntaxKind.AssertDirective:
                Assertion(statement);
                break;
            case SyntaxKind.ErrorDirective:
                Refuse(statement);
                break;
            case SyntaxKind.LabeledLine:
                foreach (var child in statement.ChildNodes)
                {
                    if (child.Kind != SyntaxKind.Label)
                        Statement(child);
                }
                break;
            default:
                break;
        }
    }

    /// <summary>
    /// The addressing mode: the narrowest the instruction offers that is at least as
    /// wide as the operand, with the choice written into the output as a prefix when the
    /// instruction offers more than one width for that shape.
    /// </summary>
    private void Instruction(SyntaxNode statement)
    {
        if (statement.ChildTokens.Length == 0)
            return;
        var mnemonic = statement.ChildTokens[0];
        var available = Instructions.Modes(cpu, mnemonic.Text);
        if (available.Count == 0)
        {
            Report(mnemonic.Span, Instructions.Has(Cpu.Wdc65C02, mnemonic.Text)
                ? $"`{mnemonic.Text}` is a 65C02 instruction, and this program is built for the 6502"
                : $"`{mnemonic.Text}` is not available on the {CpuNames.Spell(cpu)}");
            return;
        }

        var operand = statement.ChildNodes.FirstOrDefault();
        var candidates = Plausible(operand).Where(available.Contains).ToArray();
        if (candidates.Length == 0)
        {
            Report(operand?.Span ?? mnemonic.Span,
                $"`{mnemonic.Text}` does not take this operand on the {CpuNames.Spell(cpu)}");
            return;
        }

        var mode = Choose(mnemonic, operand, candidates);
        var prefix = candidates.Length > 1 ? Instructions.Prefix(mode) : null;
        lines[statement.Position] = new LineLayout(Instructions.Length(mode), mode, prefix);
    }

    /// <summary>Which of the candidate modes the operand's own width calls for.</summary>
    private AddressingMode Choose(SyntaxToken mnemonic, SyntaxNode? operand, AddressingMode[] candidates)
    {
        var widths = candidates.OrderBy(Instructions.Length).ToArray();
        if (operand is null)
            return widths[0];
        if (candidates.Length == 1)
        {
            CheckOperand(mnemonic, operand, candidates[0]);
            return candidates[0];
        }

        var required = WrittenPrefix(operand) ?? (Expression(operand) is { } expression
            ? model.AddressSizeOf(expression, segment)
            : null);

        // Where nothing says how wide it is, the reason has already been reported; the widest
        // form always reaches, so take that rather than say so twice.
        var chosen = required is null
            ? widths[^1]
            : widths.FirstOrDefault(mode => Instructions.Width(mode) >= required, widths[^1]);
        if (required is { } size && Instructions.Width(chosen) < size)
        {
            Report(operand.Span,
                $"`{mnemonic.Text}` cannot reach a {Spell(size)} address on the {CpuNames.Spell(cpu)}");
        }
        CheckOperand(mnemonic, operand, chosen);
        return chosen;
    }

    /// <summary>
    /// What the operand itself must satisfy: a control transfer takes a near target and is
    /// not sized by a prefix, and an immediate on these CPUs is one byte.
    /// </summary>
    private void CheckOperand(SyntaxToken mnemonic, SyntaxNode operand, AddressingMode mode)
    {
        if (Expression(operand) is not { } expression)
            return;

        if (mode is AddressingMode.Relative or AddressingMode.Absolute or AddressingMode.AbsoluteIndirect
            && Instructions.IsControlTransfer(mnemonic.Text))
        {
            if (WrittenPrefix(operand) is not null)
            {
                Report(operand.Span,
                    $"`{mnemonic.Text}` transfers control, and a control transfer is not sized by a prefix");
            }
            else if (model.AddressSizeOf(expression, segment) == AddressSize.Far)
            {
                Report(expression.Span,
                    $"`{mnemonic.Text}` takes a near target, and this one is far");
            }
            return;
        }

        if (mode == AddressingMode.Immediate && model.ValueOf(expression).AsNumber() is { } value
            && value is < -128 or > 255)
        {
            Report(expression.Span, $"an immediate is one byte, and {Value.Of(value)} does not fit");
        }
    }

    /// <summary>
    /// An assertion, checked here because this is the pass that walks every statement of a
    /// file with the whole program worked out. One nt65 can answer is answered; one it
    /// cannot is left for ca65 and ld65, which see the addresses nt65 never does.
    /// </summary>
    private void Assertion(SyntaxNode directive)
    {
        var assertion = Constructs.AssertionOf(directive);
        if (assertion.Condition is not { } condition)
            return;
        if (model.ValueOf(condition).AsNumber() is not { } value)
        {
            model.Check(condition, diagnostics);
            return;
        }
        if (value == 0)
            Report(directive.Span, assertion.Message ?? "this assertion does not hold", assertion.Level);
    }

    /// <summary>An <c>.error</c> the build reached: a configuration the file refuses to be built in.</summary>
    private void Refuse(SyntaxNode directive) =>
        Report(directive.Span, Constructs.AssertionOf(directive).Message ?? "this configuration is not supported");

    private void Data(SyntaxNode directive)
    {
        if (DataLengths.Of(directive, model, diagnostics) is { } length)
            lines[directive.Position] = new LineLayout(length, null, null);
    }

    private static string Spell(AddressSize size) => size switch
    {
        AddressSize.ZeroPage => "direct-page",
        AddressSize.Absolute => "absolute",
        _ => "far",
    };

    private void Report(TextSpan span, string message, Severity severity = Severity.Error) =>
        diagnostics.Add(new Diagnostic(model.Tree.GetSpan(span), severity, message));
}
