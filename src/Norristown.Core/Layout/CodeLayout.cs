using System.Collections.Immutable;
using Norristown.Processor;
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
public sealed partial class CodeLayout
{
    /// <summary>
    /// How many statements one file's expansions may lay out before nt65 gives up. The
    /// recursion check bounds each expansion on its own, but a chain of macros over long
    /// lists is not bounded by it, and neither is a program that simply asks for too
    /// much.
    /// </summary>
    private const int MaximumStatements = 65536;

    // What laying out a statement dispatches through: one method per kind of statement.
    private readonly Statements statements;
    private readonly SemanticModel model;
    private readonly Cpu cpu;

    // What the processor-state analysis found reaching each statement, which sizes a 65816
    // immediate and times its instructions. Null on the first walk, before there is any.
    private readonly IProcessorStates? states;
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

    // The routines holding an instruction this CPU does not have, or does not take that
    // operand for. Such a line is reported and left out of the stream, so nothing downstream
    // sees it at all, and a count of what the routine costs would be a count of the rest.
    private readonly HashSet<Symbol> unlaid = [];

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

    // The stream each stream's distances are measured in since its last `.align`. Flow runs
    // across an `.align`, so it ends a run of known distances and not the stream.
    private readonly Dictionary<int, int> measuredIn = [];

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

    // The settled steps of the walk before this one, which is what a cycle span is counted
    // over: a span may be written before the code it measures, so it cannot be counted from a
    // walk that is still going. Null on every walk before the lengths stop moving.
    private readonly IReadOnlyList<Step>? counted;

    // Whether anything asked for a cycle span while there was no settled walk to count over,
    // which is what says the file is worth laying out once more with one.
    private bool wantsCycles;

    private CodeLayout(
        SemanticModel model, Cpu cpu, IProcessorStates? states,
        HashSet<(int Position, Expansion? On)> lengthened, IReadOnlySet<Symbol> measured,
        Dictionary<Symbol, long> settled, IReadOnlyList<Step>? counted = null)
    {
        this.counted = counted;
        statements = new Statements(this);
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

    /// <summary>
    /// The routines an instruction of which could not be laid out. What such a routine costs
    /// is not known: the line is not in the stream, so counting the rest would say the
    /// routine is quicker than anything it could be built as.
    /// </summary>
    public IReadOnlySet<Symbol> Unlaid => unlaid;

    /// <summary>
    /// How many bytes <paramref name="symbol"/> takes in the output, which is what
    /// <c>.spanof</c> is worth, or null when nt65 cannot say — a span with an
    /// <c>.align</c> in it depends on an address.
    /// </summary>
    public long? SpanOf(Symbol symbol) => settled.TryGetValue(symbol, out var span) ? span : null;

    /// <summary>
    /// What one pass from <paramref name="from"/> to <paramref name="to"/> costs: the fewest
    /// cycles, or with <paramref name="most"/> the most. The two are positions in one routine,
    /// and the span is the instructions from the first up to the second, which is where the pass
    /// arrives rather than a line it runs.
    /// <para>
    /// A sum along a run of instructions is a bound on one pass only where the run is one pass:
    /// a call takes however long the routine it names takes, and a loop takes its own body as
    /// many times as it turns, so the span may hold neither and says which it found.
    /// </para>
    /// </summary>
    public CycleSpan CyclesOf(Symbol from, Symbol to, bool most)
    {
        if (counted is null)
        {
            // The walk this is being asked during has not reached the code yet. Saying so puts
            // the file through one more walk, where the whole of it is there to count.
            wantsCycles = true;
            return default;
        }
        if (At(from) is not { } start)
            return new CycleSpan(null, $"nothing places `{from.DisplayName}`");
        if (At(to) is not { } end)
            return new CycleSpan(null, $"nothing places `{to.DisplayName}`");
        if (counted[start].Routine is not { } routine || counted[end].Routine != routine)
            return new CycleSpan(null, "the two are not positions in one routine");
        if (counted[start].Stream != counted[end].Stream)
            return new CycleSpan(null, "the two are not in one stream of bytes");
        if (end < start)
            return new CycleSpan(null, $"`{to.DisplayName}` comes before `{from.DisplayName}`");

        var total = new CycleCount(0);
        for (var i = start; i < end; i++)
        {
            var step = counted[i];
            if (step.IsMarker || step.Statement is StateDirectiveSyntax or FrameDirectiveSyntax)
                continue;
            if (step.Statement is not InstructionStatementSyntax instruction)
                continue;
            var mnemonic = instruction.Mnemonic.Text.ToLowerInvariant();
            if (Instructions.Facts(mnemonic).Calls)
                return new CycleSpan(null, $"a call, `{mnemonic}`, which takes as long as what it calls");
            if (Backwards(instruction, step, start, i) is { } loop)
                return new CycleSpan(null, loop);
            if (Of(step.Statement, step.On)?.Cycles is not { } cycles)
                return new CycleSpan(null, $"`{mnemonic}`, which nt65 has no count for");
            total += cycles;
        }
        return new CycleSpan(most ? total.Most : total.Least, null);
    }

    /// <summary>
    /// Why the transfer at step <paramref name="i"/> turns the span into a loop, or null when it
    /// does not: a branch or a jump that goes back to somewhere at or before itself and at or
    /// after the span's start takes the code between them again, and a target nt65 cannot
    /// follow could be any of them.
    /// </summary>
    private string? Backwards(InstructionStatementSyntax instruction, Step step, int start, int i)
    {
        var mode = Of(step.Statement, step.On)?.Mode;
        var transfer = Transfers.Of(instruction, mode);
        if (transfer is Transfer.Through or Transfer.Return)
            return null;
        var mnemonic = instruction.Mnemonic.Text.ToLowerInvariant();
        if (transfer == Transfer.Elsewhere)
            return $"`{mnemonic}`, whose target nt65 cannot follow";
        if (Targets.Of(model, Transfers.TargetOf(instruction, mode), step.On) is not { } target)
            return null;
        return At(target.Symbol) is { } landing && landing >= start && landing <= i
            ? $"a loop: `{mnemonic}` goes back to `{target.Symbol.DisplayName}`"
            : null;
    }

    /// <summary>
    /// Which settled step a symbol stands at: the step that declares it as a label, or the
    /// routine's first step for a routine's own name. Null for a symbol the walk did not place.
    /// </summary>
    private int? At(Symbol symbol)
    {
        for (var i = 0; i < counted!.Count; i++)
        {
            if (counted[i].Label == symbol || (symbol.Kind == SymbolKind.Proc && counted[i].Routine == symbol))
                return i;
        }
        return null;
    }

    /// <summary>
    /// Lays out <paramref name="model"/>'s file for <paramref name="cpu"/>. On the 65816,
    /// <paramref name="states"/> says what state reaches each statement, which is what sizes
    /// its immediates, its <c>.ensure</c> directives and its frame slots; without it the
    /// immediates are laid out a byte wide and every <c>.ensure</c> writes all it could, which
    /// is enough to find where control goes, since no edge depends on a length.
    /// </summary>
    public static CodeLayout Create(SemanticModel model, Cpu cpu, IProcessorStates? states = null)
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
            layout.Walk(model.Tree.Root.Members, from: 0);
        }
        while (layout.Lengthen() | layout.Settle());

        // A cycle span is counted over a walk that has finished, because the code it measures
        // may be written after the expression that measures it. Only a file that asks for one
        // is laid out again, so nothing else pays for it.
        if (layout.wantsCycles)
        {
            layout = new CodeLayout(model, cpu, states, lengthened, measured, settled, layout.steps);
            layout.Walk(model.Tree.Root.Members, from: 0);
        }
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
    /// A run of sibling lines and blocks. The <c>.if</c> chains among them are resolved here,
    /// because a chain is a run of siblings and only whoever walks them can see it.
    /// </summary>
    private void Walk(IReadOnlyList<SyntaxNode> children, int from)
    {
        var chain = new ConditionChain();
        for (var i = from; i < children.Count; i++)
        {
            var child = children[i];
            if (child is not BlockSyntax block)
            {
                chain.Break();
                if (child is LineSyntax line)
                    Statement(line.Statement);
                continue;
            }
            if (chain.Includes(model, block, expansion))
                WalkBlock(block, block.BlockKind);
        }
    }

    private void WalkBlock(BlockSyntax block, BlockKind kind)
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
            if (Macros.CallIn(block.Opener.Statement) is not null)
                Statement(block.Opener.Statement);
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
            if (outerTurn?.NearestCall is { } call && Exceeds(turns.Count * (block.Members.Length - 1), call))
                return;
            foreach (var turn in turns)
            {
                expansion = turn;
                Walk(block.Members, from: 1);
            }
            expansion = outerTurn;
            return;
        }

        // `.multiproc` is a repetition whose body is one routine's: it is laid out once per
        // member, as the `.each` around a `.proc` that it stands for would lay it out.
        if (kind == BlockKind.MultiProc)
        {
            if (model.FamilyAt(block.Opener.Statement) is null)
                return;
            var outerFamily = expansion;
            foreach (var turn in Repetitions.Of(model, block, outerFamily, diagnostics))
            {
                expansion = turn;
                WalkBlock(block, BlockKind.Proc);
            }
            expansion = outerFamily;
            return;
        }

        var opener = block.Opener.Statement;
        var outer = segment;
        var outerRoutine = routine;
        if (kind == BlockKind.Proc && opener is ProcDeclarationSyntax or MultiProcDeclarationSyntax)
            routine = NameOf(opener);

        // What a routine or data takes is the bytes between the two ends of its block, in its
        // own stream: a nested segment block is somewhere else and does not count.
        var spanning = kind is BlockKind.Proc or BlockKind.Data or BlockKind.DataBody or BlockKind.RecordInitializer
            && NameOf(opener) is { } named && measured.Contains(named)
            ? named
            : null;
        var opened = (Stream: Measured, Offset: filled.GetValueOrDefault(Measured));
        var placing = kind is BlockKind.Segment or BlockKind.Region;
        if (placing)
        {
            // A detour to the segment the bytes are already in goes nowhere: its contents stay
            // inline, where fall-through runs into them.
            if (Constructs.SegmentOf(opener) == segment
                && (routine is not null || streams.Count > 1) && opener is SegmentStatementSyntax detour)
            {
                Report(detour.Keyword, Catalogue.SegmentBlockRedundant.Says(segment));
            }
            segment = Constructs.SegmentOf(opener) ?? segment;
            streams.Add(nextStream++);
        }
        else
        {
            Statement(opener);
        }

        var declaresData = kind is BlockKind.Data or BlockKind.DataBody or BlockKind.RecordInitializer;
        if (declaresData)
            inData++;
        Walk(block.Members, from: 1);
        if (declaresData)
            inData--;
        if (spanning is not null && Measured == opened.Stream)
            extents[spanning] = filled.GetValueOrDefault(Measured) - opened.Offset;
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
    private void Expand(MacroCallSyntax call)
    {
        if (model.MacroAt(call) is not { Definition: BlockSyntax definition } || Expansion.Expanding(expansion, definition))
            return;
        if (Exceeds(definition.Members.Length, call))
            return;

        // An argument the parameter refuses is reported at the call, and a body laid out with it
        // would only say the same thing again from inside.
        if (!ArgumentChecks.Check(model, call, expansion, segment, (node, message) => Report(node, message)))
            return;

        // A macro with a state signature is checked where its expansion starts and where it
        // ends, so both are steps of their own.
        var outer = expansion;
        var marked = cpu == Cpu.Wdc65816 && model.MacroAt(call) is { MacroSignature: not null };
        if (marked)
            steps.Add(new Step(call, outer, routine, Stream, segment, null));
        expansion = Expansion.Of(outer, call, definition);
        Walk(definition.Members, from: 1);
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
        Report(call, Catalogue.ExpansionLimit.Says(MaximumStatements));
        return true;
    }

    /// <summary>
    /// A line naming a <c>block</c> parameter, which stands for the lines the call wrote.
    /// Those are the caller's own code, so they are laid out outside the expansion that
    /// spliced them, at a level of their own: the same block may be spliced more than once,
    /// and each splice writes the lines out again.
    /// </summary>
    private void Splice(BlockSpliceSyntax statement)
    {
        if (model.SymbolAt(statement.Name) is not { Parameter: { } parameter }
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

    private void Statement(StatementSyntax statement) => statements.Visit(statement);

    /// <summary>Marks the routine being walked as one an instruction could not be laid out in.</summary>
    private void Unlayable()
    {
        if (routine is not null)
            unlaid.Add(routine);
    }

    /// <summary>
    /// One instruction, laid out in the addressing mode it calls for: the narrowest the
    /// instruction offers that is at least as wide as the operand, with the choice written
    /// into the output as a prefix when the instruction offers more than one width for that
    /// shape.
    /// </summary>
    private void Instruction(InstructionStatementSyntax statement)
    {
        var mnemonic = statement.Mnemonic;

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
                Report(mnemonic, Catalogue.InstructionOutsideARoutine.Says("an instruction belongs"));
            return;
        }

        var available = Instructions.Modes(cpu, mnemonic.Text);
        if (available.Count == 0)
        {
            var having = CpuNames.All.Where(other => Instructions.Has(other, mnemonic.Text)).ToList();
            var spelled = having.Select(CpuNames.Spell).ToList();

            // The only CPU with it is the 6502 and its undocumented opcodes, so what the reader
            // is looking at is one of those rather than an instruction they have misplaced.
            var undocumented = having is [Cpu.Mos6502X];
            Report(mnemonic, Catalogue.InstructionNotOnCpu.Says(
                mnemonic.Text,
                CpuNames.Spell(cpu),
                spelled.Count == 0 ? ""
                    : undocumented
                        ? $", and is an undocumented opcode of the NMOS 6502, which the {CpuNames.Spell(Cpu.Mos6502X)} has"
                        : ", and is on the " + (spelled.Count == 1
                            ? spelled[0]
                            : string.Join(", ", spelled.SkipLast(1)) + " and " + spelled[^1])));
            Unlayable();
            return;
        }

        // In a macro body an `operand` parameter stands as a whole operand, so the mode and
        // the address size come from what the call gave rather than from what the body wrote.
        var written = statement.Operand;
        var substituted = Operands.Substituted(model, written, expansion);
        CheckSubstitution(substituted);

        // What an operand's expressions are worth is checked here, as a data directive's
        // are, since no symbol holds them and nothing else evaluates them with anything to say.
        foreach (var expression in written?.ChildNodes.OfType<ExpressionSyntax>() ?? [])
            model.Check(expression, diagnostics, expansion, SpanOf, CyclesOf);
        var operand = substituted?.Operand ?? written;

        var candidates = Plausible(operand).Where(available.Contains).ToArray();
        if (candidates.Length == 0 && operand is null)
        {
            Report(mnemonic, Catalogue.OperandMissing.Says(mnemonic.Text));
            Unlayable();
            return;
        }
        if (candidates.Length == 0)
        {
            Report(operand?.Tree ?? mnemonic.Parent.Tree, operand?.Span ?? mnemonic.Span,
                Catalogue.OperandNotTaken.Says(mnemonic.Text, CpuNames.Spell(cpu)));
            Unlayable();
            return;
        }

        // On the 65816 an immediate is as wide as the register it goes to, which is what the
        // analysis found reaching it. Where it found nothing it has said so, and a byte keeps
        // the rest of the file laid out.
        var state = states?.Before(statement, expansion);
        int? bits = cpu == Cpu.Wdc65816 && Instructions.SizedBy(mnemonic.Text) is { } register
            ? state?.Of(register) == Width.Sixteen ? 16 : 8
            : null;

        // A width the analysis does not know has been reported where it is needed, and a value
        // that does not fit a byte is no second mistake while nobody knows it is one byte.
        var sizeUnknown = cpu == Cpu.Wdc65816 && Instructions.SizedBy(mnemonic.Text) is { } sized
            && state?.Of(sized) is not (Width.Eight or Width.Sixteen);

        var mode = Choose(mnemonic, operand, candidates, substituted, bits, sizeUnknown);
        var prefix = candidates.Length > 1 ? Instructions.Prefix(mode) : null;
        if (mode != AddressingMode.Immediate)
            bits = null;
        var length = Instructions.Length(mode) + (bits == 16 ? 1 : 0);
        var direct = operand is not null && ThroughDirectPage(operand) ? DirectOffset(mnemonic, operand, mode, state) : null;
        if (cpu == Cpu.Wdc65816 && operand is not null && mode != AddressingMode.Immediate)
            CheckDirectPageSymbols(mnemonic, operand, mode);
        var timing = Cycles.Of(cpu, mnemonic.Text, mode, state);
        IReadOnlyList<string>? causes = timing is { } counted ? counted.Causes : null;
        Laid(statement, new LineLayout(
            length, mode, prefix, false, timing?.Count, bits,
            Slot: states?.SlotAt(statement, expansion), Direct: direct, Causes: causes));
        Place(statement, length);
        steps.Add(new Step(statement, expansion, routine, Stream, segment, null));

        // `bbr0 flags, @skip` branches to the second of its two expressions; every other
        // relative form branches to its only one.
        if (mode is AddressingMode.Relative or AddressingMode.DirectRelative && operand is not null)
        {
            var target = mode == AddressingMode.DirectRelative
                ? (operand as AbsoluteOperandSyntax)?.Second
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
    private void LongBranch(InstructionStatementSyntax statement, SyntaxToken mnemonic)
    {
        var operand = statement.Operand;
        if (operand is null || Expression(operand) is not { } target
            || !Plausible(operand).Contains(AddressingMode.Relative))
        {
            Report(operand?.Tree ?? mnemonic.Parent.Tree, operand?.Span ?? mnemonic.Span,
                Catalogue.BranchOperandNotTaken.Says(mnemonic.Text));
            return;
        }
        if (WrittenPrefix(operand) is not null)
        {
            Report(operand,
                Catalogue.TransferPrefix.Says(mnemonic.Text));
            return;
        }
        if (model.AddressSizeOf(target, segment, expansion) == AddressSize.Far)
        {
            Report(target, Catalogue.TargetTooFar.Says(mnemonic.Text));
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
    /// An assertion, checked here because this is the pass that walks every statement of a
    /// file with the whole program worked out. One nt65 can answer is answered; one it
    /// cannot is left for ca65 and ld65, which see the addresses nt65 never does.
    /// </summary>
    private void Assertion(AssertDirectiveSyntax directive)
    {
        var assertion = Constructs.AssertionOf(directive);
        if (assertion.Condition is not { } condition)
            return;
        if (model.ValueOf(condition, expansion, SpanOf, CyclesOf).AsNumber() is not { } value)
        {
            model.Check(condition, diagnostics, expansion, SpanOf, CyclesOf);
            return;
        }
        if (value == 0)
            Report(directive, Catalogue.AssertionFailed.Says(assertion.Message ?? "this assertion does not hold"));
    }

    /// <summary>
    /// An <c>.ensure</c>, which writes the <c>rep</c> and <c>sep</c> the analysis found it
    /// needs. Before the analysis has run, it is laid out writing all it could.
    /// </summary>
    private void Ensure(EnsureDirectiveSyntax directive)
    {
        var state = states?.Before(directive, expansion);

        // On the 6502 and its CMOS variants there is no processor state to set, and no `rep` or
        // `sep` to set it with, so an `.ensure` is accepted and writes nothing. That is what
        // lets one routine be written for both CPUs.
        var ensured = cpu == Cpu.Wdc65816 ? Ensured.Of(directive, state) : default;
        var cycles = new CycleCount(0);
        foreach (var flags in new[] { ensured.Reset, ensured.Set }.Where(flags => flags != 0))
            cycles += Cycles.Of(cpu, "rep", AddressingMode.Immediate, state)?.Count ?? new CycleCount(3);
        Laid(directive, new LineLayout(ensured.Length, null, null, Cycles: cycles, Ensured: ensured));
        Place(directive, ensured.Length);
        steps.Add(new Step(directive, expansion, routine, Stream, segment, null));
    }

    /// <summary>
    /// An <c>.error</c> the build reached, a configuration the file refuses to be built in, or a
    /// <c>.warning</c>, which is said and built.
    /// </summary>
    private void Refuse(ErrorDirectiveSyntax directive)
    {
        var warns = directive.Keyword.Text.Equals(".warning", StringComparison.OrdinalIgnoreCase);
        var said = Constructs.AssertionOf(directive).Message ?? "this configuration is not supported";
        Report(directive, warns ? Catalogue.ConfigWarned.Says(said) : Catalogue.ConfigRefused.Says(said));
    }

    private void Data(StatementSyntax directive)
    {
        if (DataLengths.Of(directive, model, diagnostics, expansion) is not { } length)
            return;
        if (routine is null && inData == 0 && directive is DataDirectiveSyntax { Parent: not DataDeclarationSyntax } loose)
        {
            // Bytes a macro expands outside a routine belong to a declaration as much as bytes
            // written there do, which binding could not see where the body was written.
            if (expansion?.NearestCall is not null && DataSyntax.NameOf(loose) is not (".res" or ".align"))
                Report(directive, Catalogue.PaddingOutsideARoutine.Says(loose.Directive.Text, ""));
            else if (segment is null && length != 0)
                Report(directive, Catalogue.OutsideEverySegment.Says("this"));
        }
        Laid(directive, new LineLayout(length, null, null));
        Place(directive, length);
        steps.Add(new Step(directive, expansion, routine, Stream, segment, null));
    }

    /// <summary>
    /// What a statement assembles to, whichever writing of it is asked about. An editor asks
    /// about a line rather than about one expansion of it, so it is shown the first writing.
    /// </summary>
    public LineLayout? AnyOf(StatementSyntax statement) => anyWriting.GetValueOrDefault((statement.Tree, statement.Position));

    /// <summary>
    /// The symbol a declaration declares here: the one instance of a family the turn being
    /// laid out writes, and the one name every other declaration has.
    /// </summary>
    private Symbol? NameOf(SyntaxNode declaration) => model.DeclaredBy(declaration, expansion);

    private void Report(SyntaxNode node, DiagnosticMessage message, Severity? severity = null) =>
        Report(node.Tree, node.Span, message, severity);

    private void Report(SyntaxToken token, DiagnosticMessage message, Severity? severity = null) =>
        Report(token.Parent.Tree, token.Span, message, severity);

    private void Report(SyntaxTree tree, TextSpan span, DiagnosticMessage message, Severity? severity = null) =>
        diagnostics.Add(Expansion.Problem(model.Tree, tree, span, expansion, severity, message));

    /// <summary>
    /// Something wrong with a line that may have been written in another file's macro body.
    /// A body's line is reported at the call, which is in this file and is the side that
    /// chose the arguments; the body line is named beside it.
    /// </summary>
    private void ReportOnLine(SyntaxNode node, Expansion? on, DiagnosticMessage message, DiagnosticFix? fix = null)
    {
        if (node.Tree == model.Tree)
        {
            diagnostics.Add(new Diagnostic(node.Tree.GetSpan(node.Span), message) { Fix = fix });
        }
        else if (on?.NearestCall is { } call)
        {
            diagnostics.Add(new Diagnostic(call.Tree.GetSpan(call.Span), message,
                [new RelatedSpan(node.Tree.GetSpan(node.Span), "in the macro body")]));
        }
    }

    /// <summary>
    /// What laying out one statement does, a method per kind. The work is the layout's own;
    /// this says which of it each kind asks for. A kind with no method here writes no bytes and
    /// says nothing about the state: a declaration that only names something, a directive read
    /// where its block is walked, a blank or a closing line.
    /// </summary>
    /// <param name="layout">The layout being built.</param>
    private sealed class Statements(CodeLayout layout) : SyntaxVisitor
    {
        /// <inheritdoc/>
        public override void VisitInstructionStatement(InstructionStatementSyntax node) =>
            layout.Instruction(node);

        /// <inheritdoc/>
        public override void VisitDataDirective(DataDirectiveSyntax node) => layout.Data(node);

        /// <inheritdoc/>
        public override void VisitDataValues(DataValuesSyntax node) => layout.Data(node);

        /// <summary>
        /// A data declaration's name stands where its first byte does. What it holds is laid out
        /// on its own line, or in the body it opens.
        /// </summary>
        /// <param name="node">The declaration.</param>
        public override void VisitDataDeclaration(DataDeclarationSyntax node)
        {
            layout.Mark(node);
            if (node.Directive is { } element)
            {
                layout.Data(element);
                if (DataSyntax.BodyOf(element) is null && layout.NameOf(node) is { } declared
                    && layout.measured.Contains(declared)
                    && layout.placements.GetValueOrDefault((element.Position, layout.expansion)) is { Length: >= 0 } placed)
                {
                    layout.extents[declared] = placed.Length;
                }
            }
        }

        /// <inheritdoc/>
        public override void VisitAssertDirective(AssertDirectiveSyntax node) => layout.Assertion(node);

        /// <inheritdoc/>
        public override void VisitMacroCall(MacroCallSyntax node) => layout.Expand(node);

        /// <inheritdoc/>
        public override void VisitBlockSplice(BlockSpliceSyntax node) => layout.Splice(node);

        /// <inheritdoc/>
        public override void VisitErrorDirective(ErrorDirectiveSyntax node) => layout.Refuse(node);

        /// <inheritdoc/>
        public override void VisitLabeledLine(LabeledLineSyntax node)
        {
            layout.Mark(node.Label);
            if (node.Statement is { } labelled)
                layout.Statement(labelled);
        }

        /// <summary>
        /// A routine's name stands where its first byte does, which is what a branch to it
        /// reaches. One turn of a <c>.multiproc</c> is a routine, and the member it is named
        /// after stands there.
        /// </summary>
        /// <param name="node">The declaration.</param>
        public override void VisitProcDeclaration(ProcDeclarationSyntax node) => layout.Mark(node);

        /// <inheritdoc cref="VisitProcDeclaration"/>
        public override void VisitMultiProcDeclaration(MultiProcDeclarationSyntax node) => layout.Mark(node);

        /// <summary>
        /// An annotation generates nothing and is here for the flow analysis, which reads it off
        /// the statement above it. A <c>.state</c> generates nothing either, and says what the
        /// processor state is where it stands.
        /// </summary>
        /// <param name="node">The directive.</param>
        public override void VisitNextDirective(NextDirectiveSyntax node) => NoBytes(node);

        /// <inheritdoc cref="VisitNextDirective"/>
        public override void VisitPatchDirective(PatchDirectiveSyntax node) => NoBytes(node);

        /// <inheritdoc cref="VisitNextDirective"/>
        public override void VisitStateDirective(StateDirectiveSyntax node) => NoBytes(node);

        /// <inheritdoc cref="VisitNextDirective"/>
        public override void VisitFrameDirective(FrameDirectiveSyntax node) => NoBytes(node);

        /// <inheritdoc/>
        public override void VisitEnsureDirective(EnsureDirectiveSyntax node) => layout.Ensure(node);

        /// <summary>A statement that writes no bytes and stands in the stream for what it says.</summary>
        private void NoBytes(StatementSyntax statement) => layout.steps.Add(
            new Step(statement, layout.expansion, layout.routine, layout.Stream, layout.segment, null));
    }
}
