using Norristown.Processor;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Layout;

/// <summary>
/// Represents what every line of a file assembles to. It records the addressing mode each
/// instruction gets and the length of each instruction and data directive, and reports what is
/// wrong with them on the target CPU.
/// <para>
/// Syntax does not depend on the CPU, so every operand form parses for every CPU. This is the
/// layer that decides whether the target CPU supports a given instruction and operand form.
/// </para>
/// </summary>
public sealed partial class CodeLayout
{
    private readonly SemanticModel model;
    private readonly Cpu cpu;
    private readonly Dictionary<StepKey, LineLayout> lines = [];
    private readonly Dictionary<(SyntaxTree Tree, int Position), LineLayout> anyExpansion = [];

    // These record where every line's bytes land and where every label falls among them. A
    // distance between two positions is known only when both are in the same run of bytes.
    private readonly Dictionary<StepKey, BytePosition> positions = [];
    private readonly Dictionary<(Symbol Symbol, Expansion? At), BytePosition> labels = [];

    // The 65816 instructions sized by a register that the processor state shows 8 bits wide
    // where they stand, so that they reach one byte of memory. Every other such instruction may
    // reach two, including each one in a layout made without the state.
    private readonly HashSet<StepKey> narrow = [];

    // Every statement in the order its bytes are emitted. The flow analysis reads this list,
    // because the layout walk is the one that expands the macros and unrolls the repetitions.
    private readonly List<Step> steps = [];

    // What runs from each position a `.label` names inside an instruction, by the step of the
    // `.label`, and the steps of the instructions those paths reach the start of.
    private readonly Dictionary<StepKey, HiddenPath> hidden = [];
    private readonly HashSet<StepKey> landings = [];

    // The position of each `.place` among the steps; another module's bytes go at that point.
    private readonly List<PlacePoint> placePoints = [];

    // The routines holding an instruction this CPU does not have, or does not take that
    // operand for. Such a line is reported and left out of the stream, so nothing downstream
    // sees it, and a cycle count for the routine would silently omit it. The routine is recorded
    // here so that its cost is treated as unknown.
    private readonly HashSet<Symbol> unlaid = [];

    // The number of bytes each measured routine or data declaration takes. A `.spanof` may
    // appear before the thing it measures, so the spans one walk works out are the values the
    // next walk answers with. Only what the file actually measures is tracked.
    private readonly Dictionary<Symbol, long> spans;

    // The steps of the previous walk, once the lengths have reached a fixed point. A cycle span
    // is counted over these steps, because a span may appear before the code it measures and so
    // cannot be counted from a walk that is still in progress. It is null on every walk before
    // the lengths stop changing.
    private readonly IReadOnlyList<Step>? counted;

    private CodeLayout(SemanticModel model, Cpu cpu, Dictionary<Symbol, long> spans, IReadOnlyList<Step>? counted = null)
    {
        this.model = model;
        this.cpu = cpu;
        this.spans = spans;
        this.counted = counted;
    }

    /// <summary>Gets the CPU this file was laid out for.</summary>
    public Cpu Cpu => cpu;

    /// <summary>Gets the diagnostics found while laying out the file, ordered by line and column.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; private set; } = [];

    /// <summary>
    /// Gets a value indicating whether the file's expansions went past the number of statements
    /// nt65 lays out, which is an error.
    /// </summary>
    public bool ExpansionsExceeded { get; private set; }

    /// <summary>Gets every statement of the file, in the order its bytes are emitted.</summary>
    public IReadOnlyList<Step> Steps => steps;

    /// <summary>
    /// Gets the steps of the instructions that the bytes from a <c>.label</c> inside an
    /// instruction reach the start of. Control enters each there from the hidden path as well as
    /// from the statement before it.
    /// </summary>
    internal IReadOnlySet<StepKey> Landings => landings;

    /// <summary>
    /// Gets every <c>.place</c> of the file that places a module, in the order they appear, with
    /// the position of each among <see cref="Steps"/>.
    /// </summary>
    public IReadOnlyList<PlacePoint> PlacePoints => placePoints;

    /// <summary>
    /// Gets the routines containing an instruction that could not be laid out. The cost of such a
    /// routine is not known. The line is not in the stream, so counting the other instructions
    /// would report the routine as faster than any version of it that could actually be built.
    /// </summary>
    public IReadOnlySet<Symbol> Unlaid => unlaid;

    /// <summary>
    /// Lays out <paramref name="model"/>'s file for <paramref name="cpu"/>. On the 65816,
    /// <paramref name="states"/> gives the state reaching each statement, which sizes the file's
    /// immediates, its <c>.ensure</c> directives and its frame slots. Without it, the immediates
    /// are laid out a byte wide and every <c>.ensure</c> emits every instruction it could. That
    /// is enough to find where control goes, since no edge depends on a length. On any CPU,
    /// <paramref name="flags"/> gives the flags known before each statement, so that an
    /// <c>.ensure</c> emits nothing for a flag already so.
    /// </summary>
    public static CodeLayout Create(
        SemanticModel model, Cpu cpu, IProcessorStates? states = null, Func<SyntaxNode, Expansion?, FlagValues?>? flags = null)
    {
        // Every long branch starts short, and those found out of reach are lengthened until none
        // changes. This terminates because a branch only ever grows. Only the last walk is kept,
        // because the walks before it laid out a file that differs from the one emitted.
        var lengthened = new HashSet<StepKey>();
        var measured = Extents.MeasuredIn(model);
        var spans = new Dictionary<Symbol, long>();
        Walker walker;
        bool again;
        do
        {
            walker = new Walker(new CodeLayout(model, cpu, spans), states, flags, lengthened, measured);
            walker.Walk();

            // The spans are recorded even when a branch grew, so that the next walk starts from
            // the spans this one worked out.
            var branchesGrew = walker.Lengthen();
            var spansChanged = walker.RecordSpans();
            again = branchesGrew || spansChanged;
        }
        while (again);

        // A cycle span is counted over a walk that has finished, because the code it measures
        // may appear after the expression that measures it. Only a file that asks for a span is
        // laid out again, so no other file pays for it.
        if (walker.WantsCycles)
        {
            walker = new Walker(new CodeLayout(model, cpu, spans, walker.Layout.steps), states, flags, lengthened, measured);
            walker.Walk();
        }
        return walker.Finish();
    }

    /// <summary>
    /// Returns whether an operand has a <c>d:</c> prefix, which reaches a constant address through
    /// the direct page.
    /// </summary>
    public static bool ThroughDirectPage(SyntaxNode operand) =>
        operand is AbsoluteOperandSyntax { Prefix: { } prefix } && char.ToLowerInvariant(prefix.Name.Text[0]) == 'd';

    /// <summary>
    /// Returns a value indicating whether an instruction is a <c>jmp (vector)</c> on the NMOS
    /// 6502. That processor reads the vector's high byte from the next address without carrying
    /// into the next page, so a vector at the last byte of a page has its high byte read from the
    /// first byte of that page.
    /// </summary>
    public static bool WrapsIndirectJump(Cpu cpu, MnemonicKind mnemonic, AddressingMode? mode) =>
        cpu is Cpu.Mos6502 or Cpu.Mos6502X && mnemonic == MnemonicKind.Jmp && mode == AddressingMode.AbsoluteIndirect;

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
    /// Returns the number of bytes <paramref name="symbol"/> takes in the output, which is the
    /// value of <c>.spanof</c>, or null when nt65 cannot tell. For example, a span with an
    /// <c>.align</c> in it depends on an address.
    /// </summary>
    public long? SpanOf(Symbol symbol) => spans.TryGetValue(symbol, out var span) ? span : null;

    /// <summary>
    /// Returns what runs from the position the <c>.label</c> at <paramref name="step"/> names inside
    /// an instruction, or null for any other step, and for a <c>.label</c> whose bytes nt65 could
    /// not follow.
    /// </summary>
    internal HiddenPath? HiddenPathAt(Step step) => hidden.GetValueOrDefault(step.Key);

    /// <summary>
    /// Returns the instructions the bytes from the position <paramref name="label"/> names inside
    /// an instruction run as, or null where it names no such position, or nt65 could not follow
    /// the bytes.
    /// </summary>
    public IReadOnlyList<HiddenInstruction>? HiddenInstructionsOf(Symbol label) =>
        hidden.Values.FirstOrDefault(path => path.Label == label)?.Instructions;

    /// <summary>
    /// Returns a value indicating whether a <c>.label</c> names a position inside the bytes of
    /// <paramref name="step"/>, so that the bytes run as more than the statement says.
    /// </summary>
    internal bool IsEnteredInside(Step step)
    {
        if (hidden.Count == 0 || PositionOf(step.Statement, step.On) is not { } at)
            return false;
        return hidden.Values.Any(path => PositionOf(path.Label) is { } label
            && label.Stream == at.Stream && label.Offset > at.Offset && label.Offset < at.End);
    }

    /// <summary>
    /// Returns a value indicating whether the processor state this file was laid out with shows
    /// that the 65816 instruction at <paramref name="step"/> reaches one byte of memory. The
    /// register that sizes it is then 8 bits wide there.
    /// </summary>
    internal bool ReachesOneByte(Step step) => narrow.Contains(step.Key);

    /// <summary>
    /// Returns the index among <see cref="Steps"/> of the statement whose bytes cover
    /// <paramref name="offset"/> in the run of bytes <paramref name="run"/>, or null where no
    /// statement nt65 laid out covers it. A run is the one <see cref="BytePosition.Stream"/> names.
    /// </summary>
    internal int? StepCovering(int run, long offset)
    {
        for (var index = 0; index < steps.Count; index++)
        {
            var step = steps[index];
            if (step.Label is null && PositionOf(step.Statement, step.On) is { Length: > 0 } at
                && at.Stream == run && at.Offset <= offset && offset < at.End)
            {
                return index;
            }
        }
        return null;
    }

    /// <summary>
    /// Returns what a statement assembles to in the <see cref="Expansion"/> <paramref name="on"/>,
    /// or null when it generates no bytes.
    /// </summary>
    public LineLayout? Of(SyntaxNode statement, Expansion? on = null) =>
        lines.GetValueOrDefault(StepKey.Of(statement, on));

    /// <summary>
    /// Returns what a statement assembles to in its first expansion. An editor asks about a line
    /// rather than about one expansion of it, so it is shown the first.
    /// </summary>
    public LineLayout? AnyOf(StatementSyntax statement) => anyExpansion.GetValueOrDefault((statement.Tree, statement.Position));

    /// <summary>Returns where a statement's bytes land, or null when it generates none.</summary>
    public BytePosition? PositionOf(SyntaxNode statement, Expansion? on = null) =>
        positions.TryGetValue(StepKey.Of(statement, on), out var position) ? position : null;

    /// <summary>
    /// Returns where <paramref name="label"/> stands in the stream around it, or null when the
    /// walk did not record it. A label that a macro body declares stands somewhere different in
    /// every expansion, so the expansion being asked about is part of the question.
    /// </summary>
    public BytePosition? PositionOf(Symbol label, Expansion? on = null) =>
        labels.TryGetValue((label, Expansion.Owning(on, label)), out var position) ? position : null;
}
