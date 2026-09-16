using System.Collections.Immutable;
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
    private readonly Dictionary<(int Position, Expansion? On), LineLayout> lines = [];
    private readonly List<Diagnostic> diagnostics = [];

    // Where every line's bytes land, and where every label stands among them. A distance is
    // known only within one stream, which is what branch range and the long branches read.
    private readonly Dictionary<(int Position, Expansion? On), Placement> placements = [];
    private readonly Dictionary<(Symbol Symbol, Expansion? At), Placement> labels = [];
    private readonly Dictionary<int, int> filled = [];
    private readonly List<Branch> branches = [];

    // The long branches already found out of reach, which is what carries between walks:
    // lengthening one moves everything after it, so the file is laid out again.
    private readonly HashSet<(int Position, Expansion? On)> lengthened;

    // The streams the walk is inside, innermost last. A nested segment block is a detour, so
    // the stream around it resumes where it left off.
    private readonly List<int> streams = [0];
    private int nextStream = 1;
    private string segment = SegmentTable.DefaultSegment;

    // Which turn of which repetitions, and which expansion of which macros, the walk is
    // inside. A body is laid out once per writing, and the same line can be a different
    // length on each of them.
    private Expansion? expansion;

    // How many statements the expansions have laid out. An expansion is bounded by the
    // recursion check, but a chain of macros over long lists is not, so it is counted too.
    private int expanded;

    /// <summary>
    /// How many statements one file's expansions may lay out before nt65 gives up. The
    /// recursion check bounds each expansion on its own, but a chain of macros over long
    /// lists is not bounded by it, and neither is a program that simply asks for too
    /// much.
    /// </summary>
    private const int MaximumStatements = 65536;

    private CodeLayout(SemanticModel model, Cpu cpu, HashSet<(int Position, Expansion? On)> lengthened)
    {
        this.model = model;
        this.cpu = cpu;
        this.lengthened = lengthened;
    }

    /// <summary>The CPU this file was laid out for.</summary>
    public Cpu Cpu => cpu;

    /// <summary>What the CPU makes wrong, ordered by line and column.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; private set; } = [];

    /// <summary>Lays out <paramref name="model"/>'s file for <paramref name="cpu"/>.</summary>
    public static CodeLayout Create(SemanticModel model, Cpu cpu)
    {
        // Every long branch starts short, and those found out of reach are lengthened until
        // none changes, which terminates because a branch only ever grows. Only the last
        // walk is kept: the ones before it laid out a file that is not the one written.
        var lengthened = new HashSet<(int Position, Expansion? On)>();
        CodeLayout layout;
        do
        {
            layout = new CodeLayout(model, cpu, lengthened);
            layout.WalkContainer(model.Tree.Root);
        }
        while (layout.Lengthen());

        layout.CheckBranchRange();
        layout.Diagnostics = Norristown.Diagnostics.Ordered(layout.diagnostics);
        return layout;
    }

    /// <summary>
    /// What a statement assembles to on the turn <paramref name="on"/> of the repetitions
    /// around it, or null when it generates no bytes.
    /// </summary>
    public LineLayout? Of(SyntaxNode statement, Expansion? on = null) =>
        lines.GetValueOrDefault((statement.Position, on));

    /// <summary>Where a statement's bytes land, or null when it generates none.</summary>
    public Placement? Placed(SyntaxNode statement, Expansion? on = null) =>
        placements.TryGetValue((statement.Position, on), out var placement) ? placement : null;

    /// <summary>
    /// Where <paramref name="label"/> stands in the stream around it, or null when nothing
    /// placed it. A label a macro body declares stands somewhere different at every
    /// expansion, so which writing is being asked about is part of the question.
    /// </summary>
    public Placement? Placed(Symbol label, Expansion? on = null) =>
        labels.TryGetValue((label, Expansion.Owning(on, label)), out var placement) ? placement : null;

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
            // An `operand` argument written without braces is an expression, and a plain
            // address operand by being one.
            case not (SyntaxKind.AbsoluteOperand or SyntaxKind.ImmediateOperand
                or SyntaxKind.AccumulatorOperand or SyntaxKind.IndirectOperand
                or SyntaxKind.IndexedIndirectOperand or SyntaxKind.LongIndirectOperand):
                return [AddressingMode.Direct, AddressingMode.Absolute, AddressingMode.Relative];

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
        operand.Kind is SyntaxKind.AbsoluteOperand or SyntaxKind.ImmediateOperand
            or SyntaxKind.IndirectOperand or SyntaxKind.IndexedIndirectOperand
            or SyntaxKind.LongIndirectOperand
            ? operand.ChildNodes.FirstOrDefault(c => c.Kind != SyntaxKind.AddressPrefix)
            : operand.Kind == SyntaxKind.AccumulatorOperand ? null : operand;

    private void WalkContainer(SyntaxNode container) => Walk(container.ChildNodes, from: 0);

    /// <summary>
    /// A run of sibling lines and blocks. The <c>.if</c> chains among them are resolved here,
    /// because a chain is a run of siblings and only whoever walks them can see it.
    /// </summary>
    private void Walk(IReadOnlyList<SyntaxNode> children, int from)
    {
        var chain = new ConditionChain();
        for (var i = from; i < children.Count; i++)
        {
            var child = children[i];
            if (child.Green is not GreenBlock block)
            {
                chain.Break();
                if (child.Statement is { } statement)
                    Statement(statement);
                continue;
            }
            if (chain.Includes(model, child, expansion))
                WalkBlock(child, block.BlockKind);
        }
    }

    private void WalkBlock(SyntaxNode block, BlockKind kind)
    {
        // A macro body generates nothing where it is written: it is laid out at every call
        // that expands it, and in the segment that call is in.
        if (kind == BlockKind.Macro)
            return;

        // A block argument is the call's: the line that opens it is the call, which is laid
        // out here, and its lines are laid out wherever the body splices them.
        if (kind == BlockKind.MacroBlock)
        {
            if (block.ChildNodes.Length > 0 && block.ChildNodes[0].Statement is { } call
                && Macros.CallIn(call) is not null)
            {
                Statement(call);
            }
            return;
        }

        // A repetition's body is laid out once per turn: what `.res n` reserves and how wide
        // an address `lda n` reaches both follow from the turn.
        if (Constructs.Repeats(kind))
        {
            var outerTurn = expansion;
            foreach (var turn in Repetitions.Of(model, block, outerTurn, diagnostics))
            {
                expansion = turn;
                Walk(block.ChildNodes, from: 1);
            }
            expansion = outerTurn;
            return;
        }

        var lines = block.ChildNodes;
        var outer = segment;
        if (kind == BlockKind.Segment && lines.Length > 0 && lines[0].Statement is { } opener)
        {
            segment = Constructs.SegmentOf(opener) ?? segment;
            streams.Add(nextStream++);
        }
        else if (lines.Length > 0 && lines[0].Statement is { } other)
        {
            Statement(other);
        }

        Walk(lines, from: 1);
        if (kind == BlockKind.Segment)
            streams.RemoveAt(streams.Count - 1);
        segment = outer;
    }

    /// <summary>
    /// A call, laid out as the body it expands to. The body belongs to whichever file
    /// declares the macro, and is read there and laid out here, in this call's segment and
    /// with this call's arguments.
    /// </summary>
    private void Expand(SyntaxNode call)
    {
        if (model.MacroAt(call) is not { Definition: { } definition })
            return;
        expanded += definition.ChildNodes.Length;
        if (expanded > MaximumStatements)
        {
            Report(call.Span, $"the expansions in this file come to more than {MaximumStatements} "
                + "statements, which is as far as nt65 goes");
            return;
        }

        var outer = expansion;
        expansion = Expansion.Of(outer, call, definition);
        Walk(definition.ChildNodes, from: 1);
        expansion = outer;
    }

    /// <summary>
    /// A line naming a <c>block</c> parameter, which stands for the lines the call wrote.
    /// Those are the caller's own code, so they are laid out outside the expansion that
    /// spliced them, at a level of their own: the same block may be spliced more than once,
    /// and each splice writes the lines out again.
    /// </summary>
    private void Splice(SyntaxNode statement)
    {
        if (statement.ChildTokens.Length == 0
            || model.SymbolAt(statement.ChildTokens[0]) is not { Parameter: { } parameter }
            || model.ArgumentFor(parameter.Symbol, expansion) is not { Block: { } block })
        {
            return;
        }

        var outer = expansion;
        expansion = Expansion.Spliced(outer, statement, block);
        Walk(Macros.LinesOf(block), 0);
        expansion = outer;
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
            case SyntaxKind.MacroCall:
                Expand(statement);
                break;
            case SyntaxKind.BlockSplice:
                Splice(statement);
                break;
            case SyntaxKind.ErrorDirective:
                Refuse(statement);
                break;
            case SyntaxKind.LabeledLine:
                foreach (var child in statement.ChildNodes)
                {
                    if (child.Kind == SyntaxKind.Label)
                        Mark(child);
                    else
                        Statement(child);
                }
                break;

            // A routine's name stands where its first byte does, which is what a branch to
            // it reaches.
            case SyntaxKind.ProcDeclaration:
                Mark(statement);
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

        // A long branch is not one of the CPU's instructions but a choice between two of
        // them, so it is laid out before the table has its say.
        if (SyntaxFacts.LongBranches.Contains(mnemonic.Text))
        {
            LongBranch(statement, mnemonic);
            return;
        }

        var available = Instructions.Modes(cpu, mnemonic.Text);
        if (available.Count == 0)
        {
            Report(mnemonic.Span, Instructions.Has(Cpu.Wdc65C02, mnemonic.Text)
                ? $"`{mnemonic.Text}` is a 65C02 instruction, and this program is built for the 6502"
                : $"`{mnemonic.Text}` is not available on the {CpuNames.Spell(cpu)}");
            return;
        }

        // In a macro body an `operand` parameter stands as a whole operand, so the mode and
        // the address size come from what the call gave rather than from what the body wrote.
        var written = statement.ChildNodes.FirstOrDefault();
        var substituted = Operands.Substituted(model, written, expansion);
        CheckSubstitution(substituted);
        var operand = substituted?.Operand ?? written;

        var candidates = Plausible(operand).Where(available.Contains).ToArray();
        if (candidates.Length == 0)
        {
            Report(operand?.Span ?? mnemonic.Span,
                $"`{mnemonic.Text}` does not take this operand on the {CpuNames.Spell(cpu)}");
            return;
        }

        var mode = Choose(mnemonic, operand, candidates, substituted);
        var prefix = candidates.Length > 1 ? Instructions.Prefix(mode) : null;
        var length = Instructions.Length(mode);
        lines[(statement.Position, expansion)] = new LineLayout(length, mode, prefix);
        Place(statement, length);

        // `bbr0 flags, @skip` branches to the second of its two expressions; every other
        // relative form branches to its only one.
        if (mode is AddressingMode.Relative or AddressingMode.DirectRelative && operand is not null)
        {
            var target = mode == AddressingMode.DirectRelative
                ? operand.ChildNodes.LastOrDefault(child => child.Kind != SyntaxKind.AddressPrefix)
                : Expression(operand);
            if (target is not null)
                branches.Add(new Branch(statement, expansion, target, Long: false));
        }
    }

    /// <summary>
    /// A long branch, which branches like its short form but reaches any near target. It is
    /// laid out short and lengthened only where the target turns out to be out of reach, so
    /// a forward branch to a near target keeps the short form; ca65's own package can choose
    /// that form only for a target it has already seen, so its forward branches are always
    /// long.
    /// </summary>
    private void LongBranch(SyntaxNode statement, SyntaxToken mnemonic)
    {
        var operand = statement.ChildNodes.FirstOrDefault();
        if (operand is null || Expression(operand) is not { } target
            || !Plausible(operand).Contains(AddressingMode.Relative))
        {
            Report(operand?.Span ?? mnemonic.Span,
                $"`{mnemonic.Text}` branches to a near target, and does not take this operand");
            return;
        }
        if (WrittenPrefix(operand) is not null)
        {
            Report(operand.Span,
                $"`{mnemonic.Text}` transfers control, and a control transfer is not sized by a prefix");
            return;
        }
        if (model.AddressSizeOf(target, segment, expansion) == AddressSize.Far)
        {
            Report(target.Span, $"`{mnemonic.Text}` takes a near target, and this one is far");
            return;
        }

        var over = lengthened.Contains((statement.Position, expansion));
        var length = Instructions.Length(AddressingMode.Relative)
            + (over ? Instructions.Length(AddressingMode.Absolute) : 0);
        branches.Add(new Branch(statement, expansion, target, Long: true));
        lines[(statement.Position, expansion)] = new LineLayout(length, AddressingMode.Relative, null, over);
        Place(statement, length);
    }

    /// <summary>
    /// How far a branch reaches: from the instruction after it to its target, or null when
    /// the two are not in one stream or the target is no label this file placed.
    /// </summary>
    private int? Distance(Branch branch)
    {
        if (placements.GetValueOrDefault((branch.Statement.Position, branch.On)) is not { Length: > 0 } from)
            return null;
        return Located(branch.Target, branch.On) is { } to && to.Stream == from.Stream
            ? to.Offset - from.End
            : null;
    }

    /// <summary>
    /// Where the label an expression names stands. A macro parameter stands for what the
    /// call gave it, and what the call gave was written in the caller, so it is placed at
    /// the caller's level rather than at the body's.
    /// </summary>
    private Placement? Located(SyntaxNode expression, Expansion? on)
    {
        if (expression.Kind != SyntaxKind.NameExpression || model.SymbolOf(expression) is not { } symbol)
            return null;
        if (symbol.Kind == SymbolKind.MacroParameter)
        {
            return model.GivenAt(symbol, on) is { Argument.Value: { } given, Caller: var caller }
                ? Located(given, caller)
                : null;
        }
        return labels.TryGetValue((symbol, Expansion.Owning(on, symbol)), out var placement)
            ? placement
            : null;
    }

    /// <summary>
    /// Lengthens every long branch this walk found out of reach, and says whether any
    /// changed. A branch only ever grows, so asking again settles.
    /// </summary>
    private bool Lengthen()
    {
        var changed = false;
        foreach (var branch in branches)
        {
            var at = (branch.Statement.Position, branch.On);
            // A target at a distance nt65 does not know is always long: nothing says it is
            // near enough, and a branch that cannot reach is no branch at all.
            if (!branch.Long || lengthened.Contains(at) || Distance(branch) is { } reach && InRange(reach))
                continue;
            lengthened.Add(at);
            changed = true;
        }
        return changed;
    }

    /// <summary>
    /// The short branches that cannot reach what they name. A distance nt65 does not know is
    /// left to ca65, whose own check stands; a long branch has been lengthened rather than
    /// reported.
    /// </summary>
    private void CheckBranchRange()
    {
        foreach (var branch in branches)
        {
            if (branch.Long || Distance(branch) is not { } reach || InRange(reach))
                continue;
            var mnemonic = branch.Statement.ChildTokens[0].Text;
            var longer = "j" + mnemonic[1..];
            var fix = SyntaxFacts.LongBranches.Contains(longer)
                ? $". `{longer}` reaches any near target"
                : "";
            ReportOnLine(branch.Target, branch.On,
                $"`{mnemonic}` would branch {reach} bytes, and a branch reaches only -128 to 127{fix}");
        }
    }

    /// <summary>Whether a distance is one a branch can reach.</summary>
    private static bool InRange(int reach) => reach is >= -128 and <= 127;

    /// <summary>
    /// Whether the argument's mode has the next byte the body asked for. An immediate, the
    /// accumulator, an indirect operand and a stack-relative one have no second byte to
    /// name, and <c>.byteof</c> shifts an immediate rather than adding to it.
    /// </summary>
    private void CheckSubstitution(OperandSubstitution? substituted)
    {
        if (substituted is not { } given || given.HasNextByte)
            return;
        if (given.ByteOf && given.Operand.Kind == SyntaxKind.ImmediateOperand)
            return;
        if (!given.ByteOf && given.Offset == 0)
            return;

        // The pair is what is wrong — this body line with this argument — so it is reported
        // at the call, which is the side that can change it, and the body line is named.
        var what = given.ByteOf ? "`.byteof`" : $"`{given.Parameter.Name} + n`";
        ReportPaired(given.At, $"{what} needs an operand with a next byte, and `{given.Parameter.Name}` "
            + $"is `{given.Mode}` here");
    }

    /// <summary>
    /// Something that is only wrong for these arguments: reported at the call, which is the
    /// side that can change them, with the body line that wrote it named beside it.
    /// </summary>
    private void ReportPaired(SyntaxNode inTheBody, string message)
    {
        if (expansion?.NearestCall is not { } call)
            return;
        diagnostics.Add(new Diagnostic(call.Tree.GetSpan(call.Span), Severity.Error, message,
            [new RelatedSpan(inTheBody.Tree.GetSpan(inTheBody.Span), "in the macro body")]));
    }

    /// <summary>Which of the candidate modes the operand's own width calls for.</summary>
    private AddressingMode Choose(
        SyntaxToken mnemonic, SyntaxNode? operand, AddressingMode[] candidates,
        OperandSubstitution? substituted)
    {
        var widths = candidates.OrderBy(Instructions.Length).ToArray();
        if (operand is null)
            return widths[0];
        if (candidates.Length == 1)
        {
            CheckOperand(mnemonic, operand, candidates[0], substituted);
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
            Report(operand.Span,
                $"`{mnemonic.Text}` cannot reach a {Spell(size)} address on the {CpuNames.Spell(cpu)}");
        }
        CheckOperand(mnemonic, operand, chosen, substituted);
        return chosen;
    }

    /// <summary>
    /// What the operand itself must satisfy: a control transfer takes a near target and is
    /// not sized by a prefix, and an immediate on these CPUs is one byte.
    /// </summary>
    private void CheckOperand(
        SyntaxToken mnemonic, SyntaxNode operand, AddressingMode mode, OperandSubstitution? substituted)
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
            else if (model.AddressSizeOf(expression, segment, expansion) == AddressSize.Far)
            {
                Report(expression.Span,
                    $"`{mnemonic.Text}` takes a near target, and this one is far");
            }
            return;
        }

        // `.byteof` takes one byte of the value, so the value it is taken from is not the
        // one that has to fit.
        if (mode == AddressingMode.Immediate && substituted is not { ByteOf: true }
            && model.ValueOf(expression, expansion).AsNumber() is { } value
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
        if (model.ValueOf(condition, expansion).AsNumber() is not { } value)
        {
            model.Check(condition, diagnostics, expansion);
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
        if (DataLengths.Of(directive, model, diagnostics, expansion) is not { } length)
            return;
        lines[(directive.Position, expansion)] = new LineLayout(length, null, null);
        Place(directive, length);
    }

    /// <summary>The stream the walk is writing into.</summary>
    private int Stream => streams[^1];

    /// <summary>
    /// Records where a line's bytes land and moves the stream on. An <c>.align</c> ends the
    /// stream instead: how many bytes it generates depends on an address, so nothing after
    /// it stands at a distance nt65 knows from anything before it.
    /// </summary>
    private void Place(SyntaxNode statement, int length)
    {
        var offset = filled.GetValueOrDefault(Stream);
        placements[(statement.Position, expansion)] = new Placement(Stream, offset, length);
        if (length == DataLengths.Unpredictable)
            streams[^1] = nextStream++;
        else
            filled[Stream] = offset + length;
    }

    /// <summary>
    /// Records where a label stands: at the first byte generated after it, which is the
    /// address a branch to it reaches.
    /// </summary>
    private void Mark(SyntaxNode declaration)
    {
        foreach (var token in declaration.ChildTokens)
        {
            if (token.Kind is not (SyntaxKind.Identifier or SyntaxKind.CheapLocal
                or SyntaxKind.Register or SyntaxKind.Mnemonic))
            {
                continue;
            }
            if (model.SymbolAt(token) is { } symbol)
                labels[(symbol, Expansion.Owning(expansion, symbol))] = new Placement(Stream, filled.GetValueOrDefault(Stream), 0);
            return;
        }
    }

    private static string Spell(AddressSize size) => size switch
    {
        AddressSize.ZeroPage => "direct-page",
        AddressSize.Absolute => "absolute",
        _ => "far",
    };

    private void Report(TextSpan span, string message, Severity severity = Severity.Error) =>
        diagnostics.Add(new Diagnostic(model.Tree.GetSpan(span), severity, message));

    /// <summary>
    /// Something wrong with a line that may have been written in another file's macro body.
    /// A body's line is reported at the call, which is in this file and is the side that
    /// chose the arguments; the body line is named beside it.
    /// </summary>
    private void ReportOnLine(SyntaxNode node, Expansion? on, string message)
    {
        if (node.Tree == model.Tree)
        {
            diagnostics.Add(new Diagnostic(node.Tree.GetSpan(node.Span), Severity.Error, message));
        }
        else if (on?.NearestCall is { } call)
        {
            diagnostics.Add(new Diagnostic(call.Tree.GetSpan(call.Span), Severity.Error, message,
                [new RelatedSpan(node.Tree.GetSpan(node.Span), "in the macro body")]));
        }
    }

    /// <summary>
    /// A branch whose reach nt65 can check: where it stands, and the target it was written
    /// with. A long branch is here too, because the same distance is what decides its form.
    /// </summary>
    private readonly record struct Branch(SyntaxNode Statement, Expansion? On, SyntaxNode Target, bool Long);
}
