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
    private readonly Dictionary<(int Position, Expansion? On), LineLayout> lines = [];
    private readonly Dictionary<(SyntaxTree Tree, int Position), LineLayout> anyExpansion = [];

    // These record where every line's bytes land and where every label falls among them. A
    // distance between two positions is known only when both are in the same run of bytes.
    private readonly Dictionary<(int Position, Expansion? On), BytePosition> positions = [];
    private readonly Dictionary<(Symbol Symbol, Expansion? At), BytePosition> labels = [];

    // Every statement in the order its bytes are emitted. The flow analysis reads this list,
    // because the layout walk is the one that expands the macros and unrolls the repetitions.
    private readonly List<Step> steps = [];

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
    /// is enough to find where control goes, since no edge depends on a length.
    /// </summary>
    public static CodeLayout Create(SemanticModel model, Cpu cpu, IProcessorStates? states = null)
    {
        // Every long branch starts short, and those found out of reach are lengthened until none
        // changes. This terminates because a branch only ever grows. Only the last walk is kept,
        // because the walks before it laid out a file that differs from the one emitted.
        var lengthened = new HashSet<(int Position, Expansion? On)>();
        var measured = Extents.MeasuredIn(model);
        var spans = new Dictionary<Symbol, long>();
        Walker walker;
        do
        {
            walker = new Walker(new CodeLayout(model, cpu, spans), states, lengthened, measured);
            walker.Walk();
        }
        while (walker.Lengthen() | walker.RecordSpans());

        // A cycle span is counted over a walk that has finished, because the code it measures
        // may appear after the expression that measures it. Only a file that asks for a span is
        // laid out again, so no other file pays for it.
        if (walker.WantsCycles)
        {
            walker = new Walker(new CodeLayout(model, cpu, spans, walker.Layout.steps), states, lengthened, measured);
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
    /// Returns the cost in cycles of one pass from <paramref name="from"/> to
    /// <paramref name="to"/>, as the lower bound or, when <paramref name="upperBound"/> is true, the
    /// upper bound. The two must be positions in one routine. The span counts the instructions
    /// from the first up to, but not including, the second, because <paramref name="to"/> is
    /// where the pass arrives rather than an instruction it runs.
    /// <para>
    /// Summing a run of instructions bounds one pass only when the run executes straight through
    /// once. A call takes as long as the called routine takes, and a loop runs its body as many
    /// times as it iterates. A span that contains a call or a loop therefore has no count, and
    /// the result gives the reason instead.
    /// </para>
    /// <para>
    /// A layout that no cycle span asked about while it was laid out has no completed walk to
    /// count over, and returns no count.
    /// </para>
    /// </summary>
    public CycleSpan CyclesOf(Symbol from, Symbol to, bool upperBound)
    {
        if (counted is null)
            return default;
        if (At(from) is not { } start)
            return new CycleSpan(null, $"`{from.DisplayName}` is not in any laid-out code");
        if (At(to) is not { } end)
            return new CycleSpan(null, $"`{to.DisplayName}` is not in any laid-out code");
        if (counted[start].Routine is not { } routine || counted[end].Routine != routine)
            return new CycleSpan(null, "the two positions are in different routines");
        if (counted[start].Stream != counted[end].Stream)
            return new CycleSpan(null, "the two positions are in different segment blocks");
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
            var mnemonic = SyntaxFacts.TextOf(instruction.MnemonicKind);
            if (Instructions.Facts(instruction.MnemonicKind).Control == Control.Calls)
                return new CycleSpan(null, $"the span contains a call, `{mnemonic}`, whose time depends on the routine it calls");
            if (Backwards(instruction, step, start, i) is { } loop)
                return new CycleSpan(null, loop);
            if (Of(step.Statement, step.On)?.Cycles is not { } cycles)
                return new CycleSpan(null, $"nt65 has no cycle count for `{mnemonic}`");
            total += cycles;
        }
        return new CycleSpan(upperBound ? total.Maximum : total.Minimum, null);
    }

    /// <summary>
    /// Returns what a statement assembles to in the <see cref="Expansion"/> <paramref name="on"/>,
    /// or null when it generates no bytes.
    /// </summary>
    public LineLayout? Of(SyntaxNode statement, Expansion? on = null) =>
        lines.GetValueOrDefault((statement.Position, on));

    /// <summary>
    /// Returns what a statement assembles to in its first expansion. An editor asks about a line
    /// rather than about one expansion of it, so it is shown the first.
    /// </summary>
    public LineLayout? AnyOf(StatementSyntax statement) => anyExpansion.GetValueOrDefault((statement.Tree, statement.Position));

    /// <summary>Returns where a statement's bytes land, or null when it generates none.</summary>
    public BytePosition? PositionOf(SyntaxNode statement, Expansion? on = null) =>
        positions.TryGetValue((statement.Position, on), out var position) ? position : null;

    /// <summary>
    /// Returns where <paramref name="label"/> stands in the stream around it, or null when the
    /// walk did not record it. A label that a macro body declares stands somewhere different in
    /// every expansion, so the expansion being asked about is part of the question.
    /// </summary>
    public BytePosition? PositionOf(Symbol label, Expansion? on = null) =>
        labels.TryGetValue((label, Expansion.Owning(on, label)), out var position) ? position : null;

    /// <summary>
    /// Returns why the transfer at step <paramref name="i"/> turns the span into a loop, or null
    /// when it does not. A branch or jump back to a point between the span's start and itself
    /// runs the code between them again. A transfer whose target nt65 cannot follow might go to
    /// any such point, so it is treated the same way.
    /// </summary>
    private string? Backwards(InstructionStatementSyntax instruction, Step step, int start, int i)
    {
        var mode = Of(step.Statement, step.On)?.Mode;
        var transfer = Transfers.Of(instruction, mode);
        if (transfer is Transfer.Through or Transfer.Return)
            return null;
        var mnemonic = SyntaxFacts.TextOf(instruction.MnemonicKind);
        if (transfer == Transfer.Elsewhere)
            return $"the span contains `{mnemonic}`, whose target nt65 cannot follow";
        if (Targets.Of(model, Transfers.TargetOf(instruction, mode), step.On) is not { } target)
            return null;
        return At(target.Symbol) is { } landing && landing >= start && landing <= i
            ? $"the span contains a loop: `{mnemonic}` goes back to `{target.Symbol.DisplayName}`"
            : null;
    }

    /// <summary>
    /// Returns the index of the completed walk's step at which a symbol stands. That is the step
    /// that declares it as a label or, for a routine's own name, the routine's first step. Returns
    /// null for a symbol the walk did not reach.
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
}
