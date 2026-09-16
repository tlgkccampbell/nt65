using System.Collections.Immutable;
using Norristown.Flow;
using Norristown.Project;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Layout;

/// <summary>
/// What every line of a file assembles to: the addressing mode each instruction gets, how
/// long each instruction and data directive is, and everything the CPU makes wrong about them.
/// <para>
/// Syntax does not depend on the CPU, so every operand form parses everywhere; this is the
/// layer that says whether the target has it.
/// </para>
/// </summary>
public sealed class CodeLayout
{
    private readonly SemanticModel model;
    private readonly Cpu cpu;

    // What the processor-state analysis found reaching each statement, which sizes a 65816
    // immediate and times its instructions. Null on the first walk, before there is any.
    private readonly StateAnalysis? states;
    private readonly Dictionary<(int Position, Expansion? On), LineLayout> lines = [];
    private readonly Dictionary<(SyntaxTree Tree, int Position), LineLayout> anyWriting = [];
    private readonly List<Diagnostic> diagnostics = [];

    // Where every line's bytes land, and where every label stands among them. A distance is
    // known only within one stream, which is what branch range and the long branches read.
    private readonly Dictionary<(int Position, Expansion? On), Placement> placements = [];
    private readonly Dictionary<(Symbol Symbol, Expansion? At), Placement> labels = [];
    private readonly Dictionary<int, int> filled = [];
    private readonly List<Branch> branches = [];

    // Every statement in the order its bytes are written, which is what the flow analysis
    // reads: it is this walk that expands the macros and unrolls the repetitions.
    private readonly List<Step> steps = [];

    // The long branches already found out of reach, which is what carries between walks:
    // lengthening one moves everything after it, so the file is laid out again.
    private readonly HashSet<(int Position, Expansion? On)> lengthened;

    // How many bytes each measured routine or data declaration takes. A `.spanof` may
    // be written before the thing it measures, so what one walk works out is what the next
    // one answers with; only what the file actually measures is tracked.
    private readonly IReadOnlySet<Symbol> measured;
    private readonly Dictionary<Symbol, long> settled;
    private readonly Dictionary<Symbol, long> extents = [];

    // The streams the walk is inside, innermost last. A nested segment block is a detour, so
    // the stream around it resumes where it left off.
    private readonly List<int> streams = [0];
    private int nextStream = 1;

    // The segment the walk is placing bytes in, or null before any region or block names one.
    private string? segment;

    // The routine the walk is inside, which every statement of it belongs to.
    private Symbol? routine;

    // How many data declarations the walk is inside, whose contents are the declaration's.
    private int inData;

    // Which turn of which repetitions, and which expansion of which macros, the walk is
    // inside. A body is laid out once per writing, and the same line can be a different
    // length on each of them.
    private Expansion? expansion;

    // How many statements the expansions have laid out. An expansion is bounded by the
    // recursion check, but a chain of macros over long lists is not, so it is counted too.
    private int expanded;

    // Whether the immediate being laid out is sized by a register whose width is not known.
    private bool sizeUnknown;

    /// <summary>
    /// How many statements one file's expansions may lay out before nt65 gives up. The
    /// recursion check bounds each expansion on its own, but a chain of macros over long
    /// lists is not bounded by it, and neither is a program that simply asks for too
    /// much.
    /// </summary>
    private const int MaximumStatements = 65536;

    private CodeLayout(
        SemanticModel model, Cpu cpu, StateAnalysis? states,
        HashSet<(int Position, Expansion? On)> lengthened, IReadOnlySet<Symbol> measured,
        Dictionary<Symbol, long> settled)
    {
        this.model = model;
        this.cpu = cpu;
        this.states = states;
        this.lengthened = lengthened;
        this.measured = measured;
        this.settled = settled;
    }

    /// <summary>The CPU this file was laid out for.</summary>
    public Cpu Cpu => cpu;

    /// <summary>What the CPU makes wrong, ordered by line and column.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; private set; } = [];

    /// <summary>Whether the file's expansions went past the most nt65 lays out, which is an error.</summary>
    public bool ExpansionsExceeded => expanded > MaximumStatements;

    /// <summary>Every statement of the file, in the order its bytes are written.</summary>
    public IReadOnlyList<Step> Steps => steps;

    /// <summary>The modes a plain address can be: sized by its width, or a branch target.</summary>
    private static AddressingMode[] Unindexed =>
    [
        AddressingMode.Direct, AddressingMode.Absolute, AddressingMode.Long,
        AddressingMode.Relative, AddressingMode.RelativeLong,
    ];

    /// <summary>
    /// How many bytes <paramref name="symbol"/> takes in the output, which is what
    /// <c>.spanof</c> is worth, or null when nt65 cannot say — a span with an
    /// <c>.align</c> in it depends on an address.
    /// </summary>
    public long? SpanOf(Symbol symbol) => settled.TryGetValue(symbol, out var span) ? span : null;

    /// <summary>
    /// Lays out <paramref name="model"/>'s file for <paramref name="cpu"/>. On the 65816,
    /// <paramref name="states"/> says what state reaches each statement, which is what sizes
    /// its immediates, its <c>.ensure</c> directives and its frame slots; without it the
    /// immediates are laid out a byte wide and every <c>.ensure</c> writes all it could, which
    /// is enough to find where control goes, since no edge depends on a length.
    /// </summary>
    public static CodeLayout Create(SemanticModel model, Cpu cpu, StateAnalysis? states = null)
    {
        // Every long branch starts short, and those found out of reach are lengthened until
        // none changes, which terminates because a branch only ever grows. Only the last
        // walk is kept: the ones before it laid out a file that is not the one written.
        var lengthened = new HashSet<(int Position, Expansion? On)>();
        var measured = Extents.MeasuredIn(model);
        var settled = new Dictionary<Symbol, long>();
        CodeLayout layout;
        do
        {
            layout = new CodeLayout(model, cpu, states, lengthened, measured, settled);
            layout.WalkContainer(model.Tree.Root);
        }
        while (layout.Lengthen() | layout.Settle());

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
                return Unindexed;

            case SyntaxKind.AccumulatorOperand:
                return [AddressingMode.Accumulator];

            // Two immediates are the source and destination banks of `mvn` and `mvp`.
            case SyntaxKind.ImmediateOperand:
                return operand.ChildNodes.Length > 1 ? [AddressingMode.BlockMove] : [AddressingMode.Immediate];
            case SyntaxKind.IndirectOperand:
                return IndexedBy(operand, "y")
                    ? [AddressingMode.DirectIndirectY]
                    : [AddressingMode.DirectIndirect, AddressingMode.AbsoluteIndirect];
            case SyntaxKind.IndexedIndirectOperand:
                if (IndexedBy(operand, "s"))
                    return IndexedBy(operand, "y") ? [AddressingMode.StackRelativeIndirectY] : [];
                return IndexedBy(operand, "x")
                    ? [AddressingMode.DirectIndirectX, AddressingMode.AbsoluteIndirectX]
                    : [];
            case SyntaxKind.LongIndirectOperand:
                return IndexedBy(operand, "y")
                    ? [AddressingMode.DirectIndirectLongY]
                    : [AddressingMode.DirectIndirectLong, AddressingMode.AbsoluteIndirectLong];
            default:
                if (IndexedBy(operand, "x"))
                    return [AddressingMode.DirectX, AddressingMode.AbsoluteX, AddressingMode.LongX];
                if (IndexedBy(operand, "y"))
                    return [AddressingMode.DirectY, AddressingMode.AbsoluteY];
                if (IndexedBy(operand, "s"))
                    return [AddressingMode.StackRelative];

                // A second expression rather than an index register: the branch target of
                // `bbr0 flags, @skip`.
                return operand.ChildNodes.Count(c => c.Kind != SyntaxKind.AddressPrefix) > 1
                    ? [AddressingMode.DirectRelative]
                    : Unindexed;
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
        var prefix = operand.ChildNodes.FirstOrDefault(c => c.Kind == SyntaxKind.AddressPrefix);
        if (prefix is null || prefix.ChildTokens.Length == 0)
            return null;
        return char.ToLowerInvariant(prefix.ChildTokens[0].Text[0]) switch
        {
            'z' or 'd' => AddressSize.ZeroPage,
            'a' => AddressSize.Absolute,
            'f' => AddressSize.Far,
            _ => null,
        };
    }

    /// <summary>Whether an operand is written <c>d:</c>, reaching a constant address through the direct page.</summary>
    public static bool ThroughDirectPage(SyntaxNode operand) =>
        operand.ChildNodes.FirstOrDefault(c => c.Kind == SyntaxKind.AddressPrefix) is { ChildTokens: [var prefix, ..] }
        && char.ToLowerInvariant(prefix.Text[0]) == 'd';

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
        // that expands it, and in the segment that call is in. A type's members are room in
        // whatever is declared with the type, and generate nothing where they are written.
        if (kind is BlockKind.Macro or BlockKind.Struct or BlockKind.Union or BlockKind.Enum)
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
            var turns = Repetitions.Of(model, block, outerTurn, diagnostics);

            // A repetition inside an expansion writes its body out once per turn, and every
            // turn counts towards the bound, which is checked before any of them is laid out.
            if (outerTurn?.NearestCall is { } call && Exceeds(turns.Count * (block.ChildNodes.Length - 1), call))
                return;
            foreach (var turn in turns)
            {
                expansion = turn;
                Walk(block.ChildNodes, from: 1);
            }
            expansion = outerTurn;
            return;
        }

        var lines = block.ChildNodes;
        var outer = segment;
        var outerRoutine = routine;
        if (kind == BlockKind.Proc && lines.Length > 0
            && lines[0].Statement is { Kind: SyntaxKind.ProcDeclaration } declaration)
        {
            routine = NameOf(declaration);
        }

        // What a routine or data takes is the bytes between the two ends of its block, in its
        // own stream: a nested segment block is somewhere else and does not count.
        var spanning = kind is BlockKind.Proc or BlockKind.Data or BlockKind.DataBody or BlockKind.RecordInitializer
            && lines.Length > 0 && lines[0].Statement is { } header && NameOf(header) is { } named && measured.Contains(named)
            ? named
            : null;
        var opened = (Stream, Offset: filled.GetValueOrDefault(Stream));
        var placing = kind is BlockKind.Segment or BlockKind.Region;
        if (placing && lines.Length > 0 && lines[0].Statement is { } opener)
        {
            // A detour to the segment the bytes are already in goes nowhere: its contents stay
            // inline, where fall-through runs into them.
            if (Constructs.SegmentOf(opener) == segment
                && (routine is not null || streams.Count > 1) && opener.ChildTokens.Length > 0)
            {
                Report(opener.ChildTokens[0], $"this block names \"{segment}\", the segment it is already in, "
                    + "so its contents would stay inline where fall-through reaches them");
            }
            segment = Constructs.SegmentOf(opener) ?? segment;
            streams.Add(nextStream++);
        }
        else if (lines.Length > 0 && lines[0].Statement is { } other)
        {
            Statement(other);
        }

        var declaresData = kind is BlockKind.Data or BlockKind.DataBody or BlockKind.RecordInitializer;
        if (declaresData)
            inData++;
        Walk(lines, from: 1);
        if (declaresData)
            inData--;
        if (spanning is not null && Stream == opened.Stream)
            extents[spanning] = filled.GetValueOrDefault(Stream) - opened.Offset;
        routine = outerRoutine;
        if (placing)
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
        if (model.MacroAt(call) is not { Definition: { } definition } || Expansion.Expanding(expansion, definition))
            return;
        if (Exceeds(definition.ChildNodes.Length, call))
            return;

        // A macro with a state signature is checked where its expansion starts and where it
        // ends, so both are steps of their own.
        var outer = expansion;
        var marked = cpu == Cpu.Wdc65816 && model.MacroAt(call) is { MacroSignature: not null };
        if (marked)
            steps.Add(new Step(call, outer, routine, Stream, segment, null));
        expansion = Expansion.Of(outer, call, definition);
        Walk(definition.ChildNodes, from: 1);
        expansion = outer;
        if (marked)
            steps.Add(new Step(call, outer, routine, Stream, segment, null, Closes: true));
    }

    /// <summary>
    /// Counts <paramref name="statements"/> more laid out by expansions, and whether that
    /// takes the file past the bound. The bound is reported once, at <paramref name="call"/>.
    /// </summary>
    private bool Exceeds(int statements, SyntaxNode call)
    {
        if (expanded > MaximumStatements)
            return true;
        expanded += statements;
        if (expanded <= MaximumStatements)
            return false;
        Report(call, $"the expansions in this file come to more than {MaximumStatements} "
            + "statements, which is as far as nt65 goes");
        return true;
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

        // A block spliced into a macro with a state signature has to leave the state as it
        // found it, which is checked across the two ends of the splice.
        var outer = expansion;
        var marked = cpu == Cpu.Wdc65816 && outer?.NearestCall is { } call && model.MacroAt(call) is { MacroSignature: not null };
        if (marked)
            steps.Add(new Step(statement, outer, routine, Stream, segment, null));
        expansion = Expansion.Spliced(outer, statement, block);
        Walk(Macros.LinesOf(block), 0);
        expansion = outer;
        if (marked)
            steps.Add(new Step(statement, outer, routine, Stream, segment, null, Closes: true));
    }

    private void Statement(SyntaxNode statement)
    {
        switch (statement.Kind)
        {
            case SyntaxKind.InstructionStatement:
                Instruction(statement);
                break;
            case SyntaxKind.DataDirective:
            case SyntaxKind.DataValues:
                Data(statement);
                break;

            // A data declaration's name stands where its first byte does. What it holds is
            // laid out on its own line, or in the body it opens.
            case SyntaxKind.DataDeclaration:
                Mark(statement);
                if (DataSyntax.ElementOf(statement) is { } element)
                {
                    Data(element);
                    if (DataSyntax.BodyOf(element) is null && NameOf(statement) is { } declared && measured.Contains(declared)
                        && placements.GetValueOrDefault((element.Position, expansion)) is { Length: >= 0 } placed)
                    {
                        extents[declared] = placed.Length;
                    }
                }
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

            // An annotation generates nothing and is here for the flow analysis, which reads
            // it off the statement above it. A `.state` generates nothing either, and says
            // what the processor state is where it stands.
            case SyntaxKind.NextDirective:
            case SyntaxKind.PatchDirective:
            case SyntaxKind.StateDirective:
            case SyntaxKind.FrameDirective:
                steps.Add(new Step(statement, expansion, routine, Stream, segment, null));
                break;
            case SyntaxKind.EnsureDirective:
                Ensure(statement);
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

        // An instruction outside a routine is code nothing runs, and is not laid out. Binding has
        // said so where it was written; what a macro expands there, binding could not see.
        if (routine is null)
        {
            if (expansion?.NearestCall is not null)
                Report(mnemonic, "an instruction belongs in a `.proc`: code outside one is reached by nothing nt65 can follow");
            return;
        }

        var available = Instructions.Modes(cpu, mnemonic.Text);
        if (available.Count == 0)
        {
            Report(mnemonic, Instructions.Has(Cpu.Wdc65C02, mnemonic.Text)
                ? $"`{mnemonic.Text}` is a 65C02 instruction, and this program is built for the 6502"
                : $"`{mnemonic.Text}` is not available on the {CpuNames.Spell(cpu)}");
            return;
        }

        // In a macro body an `operand` parameter stands as a whole operand, so the mode and
        // the address size come from what the call gave rather than from what the body wrote.
        var written = statement.ChildNodes.FirstOrDefault();
        var substituted = Operands.Substituted(model, written, expansion);
        CheckSubstitution(substituted);

        // What an operand's expressions are worth is checked here, as a data directive's
        // are, since no symbol holds them and nothing else evaluates them with anything to say.
        foreach (var expression in written?.ChildNodes.Where(child => child.Kind != SyntaxKind.AddressPrefix) ?? [])
            model.Check(expression, diagnostics, expansion, SpanOf);
        var operand = substituted?.Operand ?? written;

        var candidates = Plausible(operand).Where(available.Contains).ToArray();
        if (candidates.Length == 0 && operand is null)
        {
            Report(mnemonic, $"`{mnemonic.Text}` needs an operand");
            return;
        }
        if (candidates.Length == 0)
        {
            Report(operand?.Tree ?? mnemonic.Parent.Tree, operand?.Span ?? mnemonic.Span,
                $"`{mnemonic.Text}` does not take this operand on the {CpuNames.Spell(cpu)}");
            return;
        }

        // On the 65816 an immediate is as wide as the register it goes to, which is what the
        // analysis found reaching it. Where it found nothing it has said so, and a byte keeps
        // the rest of the file laid out.
        var state = states?.Before(statement, expansion)?.Processor;
        int? bits = cpu == Cpu.Wdc65816 && Instructions.SizedBy(mnemonic.Text) is { } register
            ? state?.Of(register) == Width.Sixteen ? 16 : 8
            : null;

        // A width the analysis does not know has been reported where it is needed, and a value
        // that does not fit a byte is no second mistake while nobody knows it is one byte.
        sizeUnknown = cpu == Cpu.Wdc65816 && Instructions.SizedBy(mnemonic.Text) is { } sized
            && state?.Of(sized) is not (Width.Eight or Width.Sixteen);

        var mode = Choose(mnemonic, operand, candidates, substituted, bits);
        var prefix = candidates.Length > 1 ? Instructions.Prefix(mode) : null;
        if (mode != AddressingMode.Immediate)
            bits = null;
        var length = Instructions.Length(mode) + (bits == 16 ? 1 : 0);
        var direct = operand is not null && ThroughDirectPage(operand) ? DirectOffset(mnemonic, operand, mode, state) : null;
        if (cpu == Cpu.Wdc65816 && operand is not null && mode != AddressingMode.Immediate)
            CheckDirectPageSymbols(mnemonic, operand, mode);
        Laid(statement, new LineLayout(
            length, mode, prefix, false, Cycles.Of(cpu, mnemonic.Text, mode, state), bits,
            Slot: states?.SlotAt(statement, expansion), Direct: direct));
        Place(statement, length);
        steps.Add(new Step(statement, expansion, routine, Stream, segment, null));

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
            Report(operand?.Tree ?? mnemonic.Parent.Tree, operand?.Span ?? mnemonic.Span,
                $"`{mnemonic.Text}` branches to a near target, and does not take this operand");
            return;
        }
        if (WrittenPrefix(operand) is not null)
        {
            Report(operand,
                $"`{mnemonic.Text}` transfers control, and a control transfer is not sized by a prefix");
            return;
        }
        if (model.AddressSizeOf(target, segment, expansion) == AddressSize.Far)
        {
            Report(target, $"`{mnemonic.Text}` takes a near target, and this one is far");
            return;
        }

        var over = lengthened.Contains((statement.Position, expansion));
        var length = Instructions.Length(AddressingMode.Relative)
            + (over ? Instructions.Length(AddressingMode.Absolute) : 0);
        branches.Add(new Branch(statement, expansion, target, Long: true));
        Laid(statement, new LineLayout(
            length, AddressingMode.Relative, null, over, Cycles.OfLongBranch(over)));
        Place(statement, length);
        steps.Add(new Step(statement, expansion, routine, Stream, segment, null));
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

    /// <summary>
    /// Takes what this walk worked out about the measured spans, and says whether any of
    /// them changed. A span is not what any length depends on here, so one more walk
    /// settles them.
    /// </summary>
    private bool Settle()
    {
        var changed = false;
        foreach (var symbol in measured)
        {
            var now = extents.TryGetValue(symbol, out var span) ? span : (long?)null;
            var before = settled.TryGetValue(symbol, out var was) ? was : (long?)null;
            if (now == before)
                continue;
            if (now is { } value)
                settled[symbol] = value;
            else
                settled.Remove(symbol);
            changed = true;
        }
        return changed;
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
        OperandSubstitution? substituted, int? bits)
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
                Report(operand, $"`{mnemonic.Text}` has no {Spell(written)} form of this operand on the {CpuNames.Spell(cpu)}");
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
                Report(operand, $"`{mnemonic.Text}` has only a {Spell(reach)} form of this operand, "
                    + $"and `{pointer.GetText().Trim()}` is {Spell(wide)}");
            }
            CheckOperand(mnemonic, operand, candidates[0], substituted, bits);
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
                $"`{mnemonic.Text}` cannot reach a {Spell(size)} address on the {CpuNames.Spell(cpu)}");
        }
        CheckOperand(mnemonic, operand, chosen, substituted, bits);
        return chosen;
    }

    /// <summary>
    /// What the operand itself must satisfy: a control transfer takes a near target and is
    /// not sized by a prefix, and an immediate fits the byte or two it is given.
    /// </summary>
    private void CheckOperand(
        SyntaxToken mnemonic, SyntaxNode operand, AddressingMode mode, OperandSubstitution? substituted,
        int? bits)
    {
        if (Expression(operand) is not { } expression)
            return;

        if (mode is AddressingMode.Relative or AddressingMode.RelativeLong or AddressingMode.Absolute
                or AddressingMode.AbsoluteIndirect or AddressingMode.AbsoluteIndirectX or AddressingMode.Long
                or AddressingMode.AbsoluteIndirectLong
            && Instructions.IsControlTransfer(mnemonic.Text))
        {
            if (WrittenPrefix(operand) is not null)
            {
                Report(operand,
                    $"`{mnemonic.Text}` transfers control, and a control transfer is not sized by a prefix");
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
        if (model.ValueOf(expression, expansion, SpanOf).AsNumber() is not { } value)
        {
            if (!sizeUnknown && DataLengths.TooWide(expression, bits == 16 ? 2 : 1,
                bits == 16 ? "this immediate is two bytes" : "an immediate is one byte", model, expansion) is { } wide)
            {
                Report(expression, wide);
            }
            return;
        }
        var high = bits == 16 ? 0xffff : 0xff;
        if (sizeUnknown && value is >= 0 and <= 0xffff)
            return;
        if (value < 0 && DataLengths.Negative(value, high) is { } negative)
            Report(expression, negative);
        else if (value < 0 || value > high)
        {
            Report(expression, bits == 16
                ? $"this immediate is two bytes, and {Value.Of(value)} does not fit"
                : $"an immediate is one byte, and {Value.Of(value)} does not fit");
        }
    }

    /// <summary>
    /// That a control transfer reaches as far as its target is: <c>jsr</c>, <c>jmp</c> and
    /// the branches a near one, <c>jsl</c> and <c>jml</c> a far one.
    /// </summary>
    private void CheckDistance(SyntaxToken mnemonic, SyntaxNode expression, AddressingMode mode)
    {
        var size = model.AddressSizeOf(expression, segment, expansion);
        if (mode != AddressingMode.Long)
        {
            if (size == AddressSize.Far)
                Report(expression, $"`{mnemonic.Text}` takes a near target, and this one is far");
            return;
        }

        // A constant address is taken as written: `jml $008000` leaves the current bank for
        // bank 0, which is what a long jump to a small number is for.
        if (size is null or AddressSize.Far || model.ValueOf(expression, expansion, SpanOf).AsNumber() is not null)
            return;
        var near = mnemonic.Text.Equals("jsl", StringComparison.OrdinalIgnoreCase) ? "jsr" : "jmp";
        Report(expression, $"`{mnemonic.Text}` takes a far target, and this one is {Spell(size.Value)}: "
            + $"`{near}` reaches it");
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
            Report(operand, $"`d:` reaches an address through the 65816's direct page, and this program is built for the {CpuNames.Spell(cpu)}");
            return null;
        }
        if (model.ValueOf(expression, expansion, SpanOf).AsNumber() is not { } address)
        {
            Report(operand, "`d:` is for a constant address: a symbol reaches the direct page through a `zp` segment");
            return null;
        }
        if (Instructions.Width(mode) != AddressSize.ZeroPage)
        {
            Report(operand, $"`d:` makes a direct operand, and `{mnemonic.Text}` has no direct form of this operand");
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
            Report(expression, $"`{symbol.DisplayName}` is in \"{segment.Name}\", reached through the direct page at "
                + $"{StateValue.Hex(page, 4)}, and is only a direct operand: as {(mode == AddressingMode.Long || mode == AddressingMode.LongX ? "a long" : "an absolute")} "
                + "operand it would reach its offset in the data bank");
        }
    }

    /// <summary>Whether an expression names a routine, which carries a signature.</summary>
    private bool NamesRoutine(SyntaxNode expression) =>
        Targets.Of(model, expression, expansion) is { Symbol.Signature: not null };

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
        if (model.ValueOf(condition, expansion, SpanOf).AsNumber() is not { } value)
        {
            model.Check(condition, diagnostics, expansion, SpanOf);
            return;
        }
        if (value == 0)
            Report(directive, assertion.Message ?? "this assertion does not hold", assertion.Level);
    }

    /// <summary>
    /// An <c>.ensure</c>, which writes the <c>rep</c> and <c>sep</c> the analysis found it
    /// needs. Before the analysis has run, it is laid out writing all it could.
    /// </summary>
    private void Ensure(SyntaxNode directive)
    {
        var state = states?.Before(directive, expansion)?.Processor;
        var ensured = Ensured.Of(directive, state);
        var cycles = new CycleCount(0);
        foreach (var flags in new[] { ensured.Reset, ensured.Set }.Where(flags => flags != 0))
            cycles += Cycles.Of(cpu, "rep", AddressingMode.Immediate, state) ?? new CycleCount(3);
        Laid(directive, new LineLayout(ensured.Length, null, null, Cycles: cycles, Ensured: ensured));
        Place(directive, ensured.Length);
        steps.Add(new Step(directive, expansion, routine, Stream, segment, null));
    }

    /// <summary>An <c>.error</c> the build reached: a configuration the file refuses to be built in.</summary>

    private void Refuse(SyntaxNode directive) =>
        Report(directive, Constructs.AssertionOf(directive).Message ?? "this configuration is not supported");

    private void Data(SyntaxNode directive)
    {
        if (DataLengths.Of(directive, model, diagnostics, expansion) is not { } length)
            return;
        if (routine is null && inData == 0 && directive is { Kind: SyntaxKind.DataDirective, Parent.Kind: not SyntaxKind.DataDeclaration })
        {
            // Bytes a macro expands outside a routine belong to a declaration as much as bytes
            // written there do, which binding could not see where the body was written.
            if (expansion?.NearestCall is not null && DataSyntax.NameOf(directive) is not (".res" or ".align"))
                Report(directive, $"`{directive.ChildTokens[0].Text}` outside a `.proc` belongs to a `.data` declaration");
            else if (segment is null && length != 0)
                Report(directive, "this is outside every segment: a `.segment NAME` region or block places it");
        }
        Laid(directive, new LineLayout(length, null, null));
        Place(directive, length);
        steps.Add(new Step(directive, expansion, routine, Stream, segment, null));
    }

    /// <summary>
    /// What a statement assembles to, whichever writing of it is asked about. An editor asks
    /// about a line rather than about one expansion of it, so it is shown the first writing.
    /// </summary>
    public LineLayout? AnyOf(SyntaxNode statement) => anyWriting.GetValueOrDefault((statement.Tree, statement.Position));

    /// <summary>The stream the walk is writing into.</summary>
    private int Stream => streams[^1];

    /// <summary>Records what a line assembles to on this writing of it.</summary>
    private void Laid(SyntaxNode statement, LineLayout laid)
    {
        lines[(statement.Position, expansion)] = laid;
        anyWriting.TryAdd((statement.Tree, statement.Position), laid);
    }

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
        if (NameOf(declaration) is not { } symbol)
            return;

        // What has an address needs a segment to have one in. A routine or data outside every
        // segment is reported where it is declared, and what is inside them is not reported again.
        if (segment is null && inData == 0
            && (declaration.Kind == SyntaxKind.ProcDeclaration || (routine is null && declaration.Kind == SyntaxKind.DataDeclaration)))
        {
            Report(declaration.Tree, symbol.NameSpan, $"`{symbol.DisplayName}` is outside every segment: "
                + "a `.segment NAME` region or block places it");
        }

        // A label a macro expands outside a routine is a position in no code, which binding
        // could not see where the body was written.
        if (routine is null && inData == 0 && symbol.Kind == SymbolKind.Label && expansion?.NearestCall is not null)
        {
            Report(declaration.Tree, symbol.NameSpan, $"`{symbol.DisplayName}` is a label outside a `.proc`: "
                + "a label is only a position in code");
        }
        labels[(symbol, Expansion.Owning(expansion, symbol))] =
            new Placement(Stream, filled.GetValueOrDefault(Stream), 0);
        steps.Add(new Step(declaration, expansion, routine, Stream, segment, symbol));
    }

    /// <summary>What a label or a routine declaration names.</summary>
    private Symbol? NameOf(SyntaxNode declaration)
    {
        foreach (var token in declaration.ChildTokens)
        {
            if (token.Kind is SyntaxKind.Identifier or SyntaxKind.CheapLocal
                or SyntaxKind.Register or SyntaxKind.Mnemonic)
            {
                return model.SymbolAt(token);
            }
        }
        return null;
    }

    private static string Spell(AddressSize size) => size switch
    {
        AddressSize.ZeroPage => "direct-page",
        AddressSize.Absolute => "absolute",
        _ => "far",
    };

    private void Report(SyntaxNode node, string message, Severity severity = Severity.Error) =>
        Report(node.Tree, node.Span, message, severity);

    private void Report(SyntaxToken token, string message, Severity severity = Severity.Error) =>
        Report(token.Parent.Tree, token.Span, message, severity);

    private void Report(SyntaxTree tree, TextSpan span, string message, Severity severity = Severity.Error) =>
        diagnostics.Add(Expansion.Problem(model.Tree, tree, span, expansion, severity, message));

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
