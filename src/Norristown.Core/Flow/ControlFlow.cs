using Norristown.Layout;
using Norristown.Processor;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// Works out where control goes inside each routine, as basic blocks and the edges between
/// them. The blocks are built from the order in which layout emitted the bytes, so the macros
/// are expanded and the repetitions unrolled before anything is asked about the path.
/// <para>
/// Every assembly-language trick is allowed, and each can be recognised from its syntax. Where
/// the operand does not say where flow goes, as with an indirect jump or a computed target, the
/// edge comes from a <c>.next</c> instead. Where there is no <c>.next</c>, the path simply ends,
/// and <see cref="Requirements"/> reports the missing annotation on every CPU.
/// </para>
/// </summary>
public sealed class ControlFlow
{
    private readonly SemanticModel model;
    private readonly CodeLayout layout;
    private readonly List<FlowRegion> regions = [];
    private readonly Dictionary<StepKey, IReadOnlyList<StatementSyntax>> annotations;
    private readonly Dictionary<StepKey, RelativeCall> relativeCalls;
    private readonly HashSet<StepKey> returnAddresses;

    // Whether each routine a call in the file reaches was taken never to return.
    private readonly Dictionary<Symbol, bool> neverReturns = [];

    // The instructions whose operands the program rewrites, found the first time any is asked
    // about. Two threads that ask at once find the same set.
    private HashSet<StepKey>? patched;
    private Dictionary<StepKey, IReadOnlyList<MnemonicKind>>? variants;
    private List<PatchVariant>? listedVariants;
    private HashSet<StepKey>? rewrittenOpcodes;
    private HashSet<StepKey>? rewrittenOperands;

    private ControlFlow(SemanticModel model, CodeLayout layout)
        : this(model, layout, [], [], [])
    {
    }

    private ControlFlow(
        SemanticModel model, CodeLayout layout,
        Dictionary<StepKey, IReadOnlyList<StatementSyntax>> annotations,
        Dictionary<StepKey, RelativeCall> relativeCalls,
        HashSet<StepKey> returnAddresses)
    {
        this.model = model;
        this.layout = layout;
        this.annotations = annotations;
        this.relativeCalls = relativeCalls;
        this.returnAddresses = returnAddresses;
    }

    /// <summary>Gets every routine in the file, one region each.</summary>
    public IReadOnlyList<FlowRegion> Regions => regions;

    /// <summary>Gets what is wrong with the paths through this file.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; private set; } = [];

    /// <summary>
    /// Gets every <c>.fallthrough</c> claiming that flow runs on into a routine that this file's own
    /// layout cannot show is next. Such a routine is in another module, or past a <c>.place</c>.
    /// Whether it is next is a question about the translation unit, answered once every file in it
    /// is laid out.
    /// </summary>
    public IReadOnlyList<RunningOn> RunningOn { get; private set; } = [];

    /// <summary>
    /// Gets what the registers hold at each statement of the file, or null before it has been
    /// worked out. It is a question about the program rather than about one file, because what a
    /// statement leaves in a register follows from what the routines above it call.
    /// </summary>
    public RegisterStates? Registers { get; internal set; }

    /// <summary>
    /// Gets what each routine keeps, as <see cref="RegisterKeeps"/> settled it across the program,
    /// or null before it has been worked out. A label that another routine enters keeps what the
    /// path from that label keeps.
    /// </summary>
    internal Func<Symbol, RoutineRegisters>? KeepsOf { get; set; }

    /// <summary>
    /// Gets what each routine reads, as <see cref="RegisterKeeps"/> settled it across the program,
    /// or null before it has been worked out.
    /// </summary>
    internal Func<Symbol, RoutineReads>? ReadsOf { get; set; }

    /// <summary>
    /// Gets what each routine leaves on its caller's stack, as <see cref="StackEffects"/> worked it
    /// out across the program. Before then, every routine leaves the stack as it found it.
    /// </summary>
    public StackEffects Effects { get; internal set; } = StackEffects.None;

    /// <summary>
    /// Gets what each routine returns with in the flags, as <see cref="FlagExits"/> worked it out
    /// across the program. Before then, every routine returns with what its signature says.
    /// </summary>
    public FlagExits FlagExits { get; internal set; } = FlagExits.None;

    /// <summary>
    /// Gets the signature each routine is analyzed with, as <see cref="InferredSignatures"/>
    /// worked it out across the program. Before then, every routine has what it declares.
    /// </summary>
    public InferredSignatures Signatures { get; internal set; } = InferredSignatures.None;

    /// <summary>
    /// Gets whether each routine a call in the file reaches was taken never to return. The answers
    /// are worked out across the program after each file is analyzed, so a file whose answers
    /// turn out different is analyzed again with them.
    /// </summary>
    internal IReadOnlyDictionary<Symbol, bool> NeverReturnsTaken => neverReturns;

    /// <summary>
    /// Gets what is known about the flags at each statement, and which conditional branches go
    /// one way only.
    /// </summary>
    internal FlagAnalysis? Flags { get; private set; }

    /// <summary>
    /// Gets each instruction that stands on a label a <c>.patch</c> names, in every expansion it
    /// is laid out in. The program rewrites such an instruction's operand as it runs, so the
    /// operand as written says only where the program starts from.
    /// </summary>
    internal IReadOnlySet<StepKey> Patched
    {
        get
        {
            if (patched is null)
                FindPatched();
            return patched!;
        }
    }

    /// <summary>
    /// Gets the instructions a <c>.patch … as</c> says each patched instruction can be turned
    /// into, in the addressing mode it is written in. A variant <see cref="PatchVariant.Problem"/>
    /// rejects is left out, and an instruction with none is not listed. Neither is one in
    /// <see cref="RewrittenOpcodes"/>, which may become anything.
    /// </summary>
    internal IReadOnlyDictionary<StepKey, IReadOnlyList<MnemonicKind>> Variants
    {
        get
        {
            if (variants is null)
                FindPatched();
            return variants!;
        }
    }

    /// <summary>Gets every variant a <c>.patch … as</c> lists, with the instruction it stands in for.</summary>
    internal IReadOnlyList<PatchVariant> ListedVariants
    {
        get
        {
            if (listedVariants is null)
                FindPatched();
            return listedVariants!;
        }
    }

    /// <summary>
    /// Gets each patched instruction whose opcode a store may write with something no
    /// <c>.patch … as</c> lists. Such an instruction may run as any instruction at all, so it may
    /// use and change every register.
    /// </summary>
    internal IReadOnlySet<StepKey> RewrittenOpcodes
    {
        get
        {
            if (rewrittenOpcodes is null)
                FindPatched();
            return rewrittenOpcodes!;
        }
    }

    /// <summary>
    /// Gets each patched instruction whose operand a store may write. Its operand as written says
    /// only where the program starts from, so nothing may be concluded from its value.
    /// </summary>
    internal IReadOnlySet<StepKey> RewrittenOperands
    {
        get
        {
            if (rewrittenOperands is null)
                FindPatched();
            return rewrittenOperands!;
        }
    }

    /// <summary>
    /// Works out where control goes in <paramref name="layout"/>'s file. A call to a routine
    /// returns with the flags <paramref name="exits"/> says it does, or, without it, with what the
    /// routine's signature says. A call to a routine that <paramref name="signatures"/> says never
    /// returns ends its path.
    /// </summary>
    public static ControlFlow Of(
        SemanticModel model, CodeLayout layout, FlagExits? exits = null, InferredSignatures? signatures = null) =>
        Build(model, layout, exits, signatures, whole: true);

    /// <summary>
    /// Returns the flags and the register constants known just before
    /// <paramref name="statement"/>, as every expansion of it agrees, or null where no path the
    /// flag analysis follows reaches it.
    /// </summary>
    public KnownValues? KnownBefore(StatementSyntax statement) =>
        Flags?.AnyBefore(statement) is { } state
            ? new KnownValues(new FlagValues(state.Known, state.Set), state.Held.A, state.Held.X, state.Held.Y)
            : null;

    /// <summary>
    /// Works out where control goes in <paramref name="layout"/>'s file, as <see cref="Of"/> does,
    /// but stops once each routine's blocks are built and its branches decided. Its loops are not
    /// counted, its paths are not costed and nothing is checked, so its regions have no costs and
    /// it reports nothing. The 65816's processor-state analysis reads no more than that, and the
    /// file is laid out again from its results.
    /// </summary>
    internal static ControlFlow Graph(
        SemanticModel model, CodeLayout layout, FlagExits exits, InferredSignatures signatures) =>
        Build(model, layout, exits, signatures, whole: false);

    /// <summary>
    /// Returns whether a symbol is data that holds addresses, which is what a table of targets is.
    /// That is data declared with <c>.addr</c> or <c>.faraddr</c>, records whose type has a member
    /// declared so, and mixed data with a member that is either.
    /// </summary>
    internal static bool IsAddressData(Symbol symbol) => symbol switch
    {
        { Kind: not SymbolKind.Data } => false,
        { Data: DataDirectiveSyntax element } => IsAddressElement(element)
            || (element.IsRecord && symbol.Type is { } type && HoldsAddresses(type, [])),
        { Data: null, Body: { } body } => body.Symbols.Any(IsAddressData),
        _ => false,
    };

    /// <summary>Returns whether a directive's element type is <c>.addr</c> or <c>.faraddr</c>.</summary>
    private static bool IsAddressElement(DataDirectiveSyntax element) =>
        element.Directive.DirectiveKind is DirectiveKind.Addr or DirectiveKind.FarAddr;

    /// <summary>
    /// Returns whether a struct or union has a member declared as an address, directly or in a
    /// record it holds. <paramref name="seen"/> holds the types already being asked about, so a
    /// type that contains itself ends the walk.
    /// </summary>
    private static bool HoldsAddresses(Symbol type, HashSet<Symbol> seen) =>
        type.IsLayout && seen.Add(type) && (type.Body?.Symbols ?? []).Any(member =>
            member.Kind == SymbolKind.Member
            && (member.Data is DataDirectiveSyntax element && IsAddressElement(element)
                || member.Type is { } inner && HoldsAddresses(inner, seen)));

    /// <summary>
    /// Returns whether a statement is a call instruction, whether or not its operand names the
    /// routine it calls.
    /// </summary>
    internal static bool IsCall(SyntaxNode statement) =>
        statement is InstructionStatementSyntax instruction && Instructions.IsCall(instruction.MnemonicKind);

    /// <summary>
    /// Returns the call a branch makes, for a branch among <paramref name="calls"/>, or null for
    /// every other statement.
    /// </summary>
    internal static RelativeCall? RelativeCallIn(
        IReadOnlyDictionary<StepKey, RelativeCall> calls, Step step) =>
        calls.TryGetValue(step.Key, out var call) ? call : null;

    /// <summary>
    /// Returns the blocks that the state after <paramref name="block"/> flows on to, within one
    /// routine. A call's edge is to the routine it calls, which is checked against its signature
    /// rather than walked into, so after a call the state goes on only to the statement after it.
    /// A jump to a routine's entry, the routine's own included, leaves this routine, because it
    /// is a tail call.
    /// </summary>
    internal static IEnumerable<int> Onward(IReadOnlyList<BasicBlock> blocks, BasicBlock block)
    {
        var calls = block.EndsInCall;
        foreach (var edge in block.Successors)
        {
            if (edge.Kind == EdgeKind.Call || (calls && edge.Kind != EdgeKind.FallThrough))
                continue;
            if (edge.Kind != EdgeKind.FallThrough && blocks[edge.To].Label is { Signature: not null })
                continue;
            yield return edge.To;
        }
    }

    /// <summary>
    /// Returns a copy of this flow for another analysis of the program to compose. Composing sets
    /// what each routine costs with its calls and which registers it keeps, and an analysis that
    /// keeps this file from an earlier one composes into the copy. The earlier analysis, which a
    /// reader on another thread may still be using, then keeps the answers it had. The blocks and
    /// everything else worked out for the file alone are shared, because nothing changes them.
    /// </summary>
    internal ControlFlow ForComposing()
    {
        var copy = new ControlFlow(model, layout, annotations, relativeCalls, returnAddresses)
        {
            Diagnostics = Diagnostics,
            RunningOn = RunningOn,
            Registers = Registers,
            KeepsOf = KeepsOf,
            ReadsOf = ReadsOf,
            Effects = Effects,
            FlagExits = FlagExits,
            Signatures = Signatures,
            Flags = Flags,
        };
        foreach (var (routine, never) in neverReturns)
            copy.neverReturns[routine] = never;
        copy.regions.AddRange(regions.Select(region => region.ForComposing()));
        return copy;
    }

    /// <summary>Returns the annotations under <paramref name="step"/>'s statement, in order.</summary>
    internal IReadOnlyList<StatementSyntax> AnnotationsOf(Step step) =>
        annotations.GetValueOrDefault(step.Key) ?? [];

    /// <summary>
    /// Returns the routine a statement calls, directly or as a relative call, or null for anything
    /// else.
    /// </summary>
    internal Symbol? CalledAt(Step step) => CalledAt(step, RelativeCallAt(step));

    /// <summary>
    /// Returns the routine a statement calls, directly or as the relative call
    /// <paramref name="relative"/>, or null for anything else.
    /// </summary>
    internal Symbol? CalledAt(Step step, RelativeCall? relative)
    {
        if (relative is { } known)
            return known.Routine;
        var statement = step.Statement;
        var mode = layout.Of(statement, step.On)?.Mode;
        return Transfers.Of(statement, mode) == Transfer.Call
            ? Targets.Of(model, Transfers.TargetOf(statement, mode), step.On)?.Symbol
            : null;
    }

    /// <summary>
    /// Returns the call a branch makes, for a branch that forms a relative call, or null for every
    /// other statement.
    /// </summary>
    internal RelativeCall? RelativeCallAt(Step step) => RelativeCallIn(relativeCalls, step);

    /// <summary>
    /// Returns whether a statement is the <c>per</c> that pushes a relative call's return address.
    /// </summary>
    internal bool IsReturnAddress(Step step) => returnAddresses.Contains(step.Key);

    /// <summary>
    /// Returns whether control continues into what follows a statement. A call does, in any form,
    /// because it returns, unless it calls a routine that never returns. On any other statement a
    /// <c>.next</c> says where flow goes, and that replaces continuing past it.
    /// </summary>
    internal bool RunsOn(Unit unit) => BasicBlock.Continues(EndOf(unit, RelativeCallAt(unit.Step)));

    /// <summary>
    /// Returns how control leaves <paramref name="unit"/>'s statement, where
    /// <paramref name="relative"/> is the relative call the statement makes, if any. This is how a
    /// block ends where the statement is its last. A jump is <see cref="BlockEnd.Jump"/> here,
    /// and <see cref="Link"/> tells a <see cref="BlockEnd.TailCall"/> apart once it knows which
    /// labels are the routine's own.
    /// </summary>
    internal BlockEnd EndOf(Unit unit, RelativeCall? relative)
    {
        var step = unit.Step;
        if (step.Statement is FallthroughDirectiveSyntax)
            return BlockEnd.Fallthrough;
        if (layout.HiddenPathAt(step) is not null)
            return BlockEnd.Jump;

        // A call returns to the statement after it, even where a `.next` lists the routines it
        // calls, unless it calls a routine that never returns.
        if (IsCall(step.Statement) || relative is not null)
        {
            return CalledAt(step, relative) is { Signature: not null } callee && NeverReturns(callee)
                ? BlockEnd.CallNeverReturns
                : BlockEnd.Call;
        }
        if (unit.Next is not null)
            return BlockEnd.Declared;
        return Transfers.Of(step.Statement, layout.Of(step.Statement, step.On)?.Mode) switch
        {
            Transfer.Branch when Flags?.ProvedAt(step) is { } proved => proved.Taken ? BlockEnd.Jump : BlockEnd.Through,
            Transfer.Branch => BlockEnd.Branch,
            Transfer.Jump => BlockEnd.Jump,
            Transfer.Elsewhere => BlockEnd.Elsewhere,
            Transfer.Return when step.Statement is InstructionStatementSyntax instruction
                && Instructions.Facts(instruction.MnemonicKind).Control == Control.Stops => BlockEnd.Stop,
            Transfer.Return => BlockEnd.Return,
            _ => BlockEnd.Through,
        };
    }

    /// <summary>
    /// Returns the labels one <c>.next</c> names. A target that is a list stands for every label in
    /// it. So does a data label whose items are all code labels or routines, each optionally minus
    /// one as in an RTS dispatch table. This is how an indirect call names the routines in its
    /// table.
    /// </summary>
    internal IEnumerable<(Symbol Symbol, Expansion? At)> Named(NextDirectiveSyntax next, Expansion? on)
    {
        foreach (var targetName in next.Targets)
        {
            // A `list` parameter names every label the call gave it.
            if (model.SymbolOf(targetName, on) is { Kind: SymbolKind.MacroParameter, Parameter.Kind: ParameterKind.List } list
                && model.GivenAt(list, on) is { Argument: var given, Caller: var caller })
            {
                foreach (var item in given.Items)
                {
                    if (Targets.Of(model, item, caller) is { } each)
                        yield return each;
                }
                continue;
            }

            if (Targets.Of(model, targetName, on) is not { } target)
                continue;
            var spread = Spread(target.Symbol, on).ToList();
            if (spread.Count > 0)
            {
                foreach (var item in spread)
                    yield return item;
                continue;
            }

            // Data that is not a table of code labels names nowhere code goes. It is reported
            // where the targets are checked rather than followed into the bytes.
            if (IsDataWithoutCodeLabels(target.Symbol, on))
                continue;
            yield return target;
        }
    }

    /// <summary>
    /// Returns the routine a <c>.fallthrough</c> names, or null where it names something else or
    /// nothing.
    /// </summary>
    internal Symbol? RoutineNamed(NameExpressionSyntax name, Expansion? on) =>
        Targets.Of(model, name, on)?.Symbol is { Kind: SymbolKind.Proc, Signature: not null } routine ? routine : null;

    /// <summary>
    /// Returns whether a symbol is data that does not spread to code labels, and so names nowhere
    /// code goes.
    /// </summary>
    internal bool IsDataWithoutCodeLabels(Symbol symbol, Expansion? on) =>
        symbol.Kind == SymbolKind.Data && !Spread(symbol, on).Any();

    /// <summary>
    /// Returns the routine emitted directly after <paramref name="region"/>'s routine in the same
    /// run of its segment's bytes, ignoring regions of other segments between them in the text.
    /// It also returns the <c>}</c> that closes this routine's body, which is where a
    /// <c>.fallthrough</c> naming the next routine goes. It returns null where anything with bytes
    /// in that segment, or nothing at all, comes next.
    /// </summary>
    internal (Symbol Routine, Span Closer)? EmittedAfter(FlowRegion region)
    {
        var steps = layout.Steps;
        var opened = -1;
        var last = -1;
        for (var i = 0; i < steps.Count; i++)
        {
            if (steps[i].Label == region.Routine && steps[i].Statement is ProcDeclarationSyntax)
                opened = i;
            if (opened >= 0 && steps[i].Routine == region.Routine && steps[i].Stream == steps[opened].Stream)
                last = i;
        }
        if (opened < 0 || steps[opened].Statement.Parent?.Parent is not BlockSyntax { Closer: { } closer })
            return null;
        var segment = steps[opened].Segment;
        var run = layout.PositionOf(region.Routine)?.Stream;
        for (var i = last + 1; i < steps.Count; i++)
        {
            var step = steps[i];
            if (step.Segment != segment)
                continue;

            // An `.align` or a `.place` in between starts another run, and a routine in a
            // different run from this one's does not directly follow it.
            if (step.Label is { Kind: SymbolKind.Proc, Signature: not null } next && step.Statement is ProcDeclarationSyntax)
                return layout.PositionOf(next)?.Stream == run ? (next, closer.Tree.GetSpan(closer.Span)) : null;
            if (step.Label is not null || layout.PositionOf(step.Statement, step.On) is { Length: not 0 })
                return null;
        }
        return null;
    }

    /// <summary>
    /// Works out where control goes in <paramref name="layout"/>'s file. With
    /// <paramref name="whole"/>, the flow is complete, as <see cref="Of"/> returns it. Without it,
    /// the flow stops once the blocks are built and the branches decided, as
    /// <see cref="Graph"/> returns it.
    /// </summary>
    private static ControlFlow Build(
        SemanticModel model, CodeLayout layout, FlagExits? exits, InferredSignatures? signatures, bool whole)
    {
        var flow = new ControlFlow(model, layout) { Signatures = signatures ?? InferredSignatures.None };
        var checks = new FlowChecks(model, layout, flow);
        var inline = Inline(model.Tree);

        // Every routine's blocks are built before any loop is counted, because a loop that calls
        // a routine in this file is counted only where that routine's body leaves the counter
        // alone.
        var built = new List<(Symbol Routine, List<Unit> Units, List<BasicBlock> Blocks, HashSet<Unit> InlineData)>();

        // A routine's bytes are one stream unless a nested segment block takes some of them
        // somewhere else. Fall-through stays inside a stream, but a jump may go from one to
        // another, so all of a routine's streams are one graph, its own stream first.
        foreach (var run in layout.Steps.Where(step => step.Routine is not null).GroupBy(step => step.Routine))
        {
            var routine = run.Key!;
            var units = flow.Units([.. run.GroupBy(step => step.Stream).SelectMany(stream => stream)]);

            // Where a block ends and what it costs depend on which branches are calls and on
            // which data a call returns past, so both are found first and handed to the blocks.
            var (calls, returnAddresses) = flow.FindRelativeCalls(units);
            var inlineData = checks.FindInlineData(units, calls);
            var blocks = flow.Blocks(units, calls, inlineData);
            foreach (var (at, call) in calls)
                flow.relativeCalls[at] = call;
            flow.returnAddresses.UnionWith(returnAddresses);
            built.Add((routine, units, blocks, inlineData));
        }

        // A branch the flags decide is a jump, or transfers nothing, before anything else reads
        // the blocks. Which instructions the program rewrites is known only once every routine's
        // annotations have been gathered. The flags and the constants follow a variant only where
        // its operand is the one written, because they depend on the operand's value.
        var variants = flow.Variants
            .Where(pair => !flow.RewrittenOperands.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        flow.Flags = new FlagAnalysis(model, layout, flow.Patched, variants, exits ?? FlagExits.None, flow.Signatures);
        foreach (var (routine, units, blocks, _) in built)
            flow.Decide(routine, units, blocks);

        if (!whole)
        {
            foreach (var (routine, _, blocks, _) in built)
            {
                var entered = blocks.Count > 0 && blocks[0].Label == routine;
                flow.regions.Add(new FlowRegion(routine, entered, blocks, default, [], inline));
            }
            return flow;
        }

        var bodies = built.ToDictionary(each => each.Routine, each => (IReadOnlyList<BasicBlock>)each.Blocks);
        foreach (var (routine, units, blocks, inlineData) in built)
        {
            // The routine is entered at the label of its own name.
            var entered = blocks.Count > 0 && blocks[0].Label == routine;
            CountedLoops.Find(model, layout, blocks, bodies);

            // A routine containing a line that layout could not lay out gets no count. The line
            // is missing from the stream, so counting the rest would pass off part as the whole.
            var (minimum, maximum, ends) = layout.Unlaid.Contains(routine)
                ? (null, null, true)
                : Paths.Through(blocks);
            var region = new FlowRegion(
                routine, entered, blocks, new RoutineCost(minimum, maximum, Calls(blocks), ends, Uncounted(blocks)),
                flow.Costed(blocks, inline), inline);
            flow.regions.Add(region);
            checks.Check(region, units, inlineData);
        }
        flow.RunningOn = checks.RunningOn;

        // The analysis of what each routine reads and keeps depends on flow it cannot see for
        // itself, so each construct that hides some flow has to declare what it hides.
        var diagnostics = new List<Diagnostic>(checks.Found);
        foreach (var region in flow.regions)
            flow.Flags.Check(region, diagnostics);
        Requirements.Check(model, layout, flow, diagnostics);

        flow.Diagnostics = Norristown.Diagnostics.Ordered(diagnostics);
        return flow;
    }

    /// <summary>
    /// Returns every <c>.scope</c> block inside a routine, as the span of the line that opens it
    /// and the span of the whole block. A <c>.scope</c> at file level is a namespace holding
    /// declarations, with no code of its own, so it is not included.
    /// </summary>
    private static List<(TextSpan Opener, TextSpan Whole)> Inline(SyntaxTree tree)
    {
        var found = new List<(TextSpan, TextSpan)>();
        Walk(tree.Root, false);
        return found;

        void Walk(SyntaxNode node, bool inProc)
        {
            foreach (var child in node.ChildNodes)
            {
                if (child is not BlockSyntax block)
                    continue;
                if (inProc && block.BlockKind == BlockKind.Scope)
                    found.Add((block.Opener.Span, block.FullSpan));
                Walk(block, inProc || block.BlockKind == BlockKind.Proc);
            }
        }
    }

    /// <summary>Returns whether any block of a part of a routine calls.</summary>
    private static bool Calls(IReadOnlyList<BasicBlock> blocks, bool[] inside)
    {
        for (var i = 0; i < blocks.Count; i++)
        {
            if (inside[i] && (blocks[i].Calls.Count > 0 || blocks[i].CallsUnknown))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Returns whether a routine calls anything, which its own cycle count does not follow into.
    /// Here a call counts only the call instruction, and what the called routine takes is that
    /// routine's own count. A <c>.fallthrough</c> into another routine is the same, since the
    /// count stops where that routine starts.
    /// </summary>
    private static bool Calls(IReadOnlyList<BasicBlock> blocks) =>
        blocks.Any(block => block.Calls.Count > 0 || block.RunsInto is not null || block.CallsUnknown);

    private static bool IsInstruction(SyntaxNode statement, params ReadOnlySpan<MnemonicKind> mnemonics) =>
        statement is InstructionStatementSyntax instruction && mnemonics.Contains(instruction.MnemonicKind);

    /// <summary>
    /// Returns whether a step takes no time because it generates nothing that runs. Such a step
    /// is a <c>.state</c>, a <c>.frame</c>, a <c>.fallthrough</c>, or a marker for either end of an
    /// expansion.
    /// </summary>
    private static bool TakesNoTime(Step step) =>
        step.Statement is StateDirectiveSyntax or FrameDirectiveSyntax or FallthroughDirectiveSyntax || step.IsMarker;

    /// <summary>
    /// Returns why a statement nt65 recognises has no count, for the editor's code lens. Without
    /// it the lens would show the routine without a count and give no reason. It returns null for
    /// a line nt65 could not lay out, which has already been reported where it appears.
    /// </summary>
    private static string? Uncounted(Step step) =>
        (step.Statement as InstructionStatementSyntax)?.MnemonicKind switch
        {
            MnemonicKind.Mvn or MnemonicKind.Mvp => "a block move takes 7 cycles per byte, and the number of bytes is in A",
            MnemonicKind.Jam => "`jam` stops the processor, and nothing after it runs until a reset",
            _ => null,
        };

    /// <summary>
    /// Returns why a routine has no count, taken from the first reached block that has no count.
    /// </summary>
    private static string? Uncounted(IReadOnlyList<BasicBlock> blocks) =>
        blocks.FirstOrDefault(block => block.IsReached && block.Uncounted is not null)?.Uncounted;

    /// <summary>Marks which blocks any path from the region's first block reaches.</summary>
    private static void Reach(List<BasicBlock> blocks)
    {
        if (blocks.Count == 0)
            return;
        foreach (var block in blocks)
            block.IsReached = false;
        var pending = new Queue<int>();
        pending.Enqueue(0);
        blocks[0].IsReached = true;
        while (pending.Count > 0)
        {
            foreach (var edge in blocks[pending.Dequeue()].Successors)
            {
                if (blocks[edge.To].IsReached)
                    continue;
                blocks[edge.To].IsReached = true;
                pending.Enqueue(edge.To);
            }
        }
    }

    /// <summary>
    /// Makes each conditional branch that the flags decide go one way only. One that is always
    /// taken becomes a jump, which is a tail call where its target is outside the routine, and
    /// one that is never taken transfers nothing. The edge it does not take is removed, and what
    /// the blocks reach is worked out again. A branch with a <c>.next</c> under it keeps the edges
    /// the <c>.next</c> names, and one whose target nt65 cannot read keeps both.
    /// </summary>
    private void Decide(Symbol routine, IReadOnlyList<Unit> units, List<BasicBlock> blocks)
    {
        var edges = units.Where(EndsBlock).Select(unit => (SyntaxNode)unit.Step.Statement)
            .Concat(units.Select(unit => unit.Next).OfType<NextDirectiveSyntax>())
            .Where(node => node.Tree == model.Tree)
            .SelectMany(node => node.DescendantTokens())
            .Select(token => token.Span)
            .ToHashSet();
        var proved = Flags!.Prove(routine, blocks, edges);
        if (proved.Count == 0)
            return;
        foreach (var (index, branch) in proved)
        {
            var block = blocks[index];
            if (block.End != BlockEnd.Branch)
                continue;
            var step = block.Steps[^1];
            var statement = (InstructionStatementSyntax)step.Statement;
            var mode = layout.Of(statement, step.On)?.Mode;
            var target = Targets.Of(model, Transfers.TargetOf(statement, mode), step.On)?.Symbol;
            var taken = block.Successors.Where(edge => edge.Kind == EdgeKind.Taken).ToList();
            if (taken.Count == 0 && target is null)
                continue;
            if (branch.Taken)
            {
                if (index + 1 < blocks.Count && blocks[index + 1].IsFallenInto)
                {
                    block.Unreach(new FlowEdge(index + 1, EdgeKind.FallThrough), blocks[index + 1]);
                    blocks[index + 1].IsFallenInto = false;
                }
                block.End = taken.Count > 0 ? BlockEnd.Jump : BlockEnd.TailCall;
                if (taken.Count == 0)
                    block.Called(target!);
            }
            else
            {
                foreach (var edge in taken)
                    block.Unreach(edge, blocks[edge.To]);
                block.End = BlockEnd.Through;
            }
            block.BranchesOut = false;
        }
        Reach(blocks);
    }

    /// <summary>
    /// Returns a table item without the <c>- 1</c> of an RTS dispatch table, which names the same
    /// label either way.
    /// </summary>
    private static SyntaxNode Stripped(SyntaxNode item) =>
        item is BinaryExpressionSyntax { OperatorToken.Kind: SyntaxKind.Minus } difference ? difference.Left : item;

    /// <summary>
    /// Returns what each inline <c>.scope</c> of a routine costs. A scope whose lines are blocks of
    /// their own costs what a pass through those blocks costs. A scope inside a single block costs
    /// what its own statements add up to, since a block runs all of it. A scope that is neither,
    /// whose first block nothing reaches, or that something branches into past its start, gets no
    /// cost, because there is no single pass through it to count.
    /// </summary>
    private List<ScopeCost> Costed(
        IReadOnlyList<BasicBlock> blocks, IReadOnlyList<(TextSpan Opener, TextSpan Whole)> inline)
    {
        var costs = new List<ScopeCost>();
        foreach (var (opener, whole) in inline)
        {
            if (Cost(blocks, whole) is { } cost)
                costs.Add(new ScopeCost(opener, cost));
        }
        return costs;
    }

    /// <summary>
    /// Returns whether <paramref name="routine"/> never returns, declared or inferred, and
    /// records the answer the flow took.
    /// </summary>
    private bool NeverReturns(Symbol routine) =>
        neverReturns[routine] = Signatures.Of(routine)?.NeverReturns == true;

    /// <summary>
    /// Returns what one pass through the part of a routine inside <paramref name="whole"/> costs.
    /// </summary>
    private RoutineCost? Cost(IReadOnlyList<BasicBlock> blocks, TextSpan whole)
    {
        if (ScopeShape.Of(blocks, whole) is not { } shape)
            return null;

        // The scope lies inside one block, so nothing in it branches. What it costs is what its
        // statements cost, and the block runs every one of them.
        if (shape.IsStraight)
            return Straight(blocks[shape.Entry], whole);

        var (entry, inside) = (shape.Entry, shape.Inside);
        var (minimum, maximum, ends) = Paths.Through(blocks, entry, at => inside[at], Paths.Costing);
        return minimum is null ? null : new RoutineCost(minimum, maximum, Calls(blocks, inside), ends);
    }

    /// <summary>
    /// Returns what the statements of <paramref name="block"/> that lie inside a span add up to.
    /// </summary>
    private RoutineCost? Straight(BasicBlock block, TextSpan whole)
    {
        var total = new CycleCount(0);
        var within = false;
        var any = false;
        foreach (var step in block.Steps)
        {
            if (step.On is null)
                within = step.Statement.Position >= whole.Start && step.Statement.Position < whole.End;
            if (!within || TakesNoTime(step))
                continue;
            if (layout.Of(step.Statement, step.On)?.Cycles is not { } cycles)
                return null;
            total += cycles;
            any = true;
        }
        return any ? new RoutineCost(total.Minimum, total.Maximum, false, true) : null;
    }

    /// <summary>
    /// Returns the statements of one region, each with the annotations under it. An
    /// annotation is about the statement above it, so it belongs to that statement rather
    /// than standing on its own.
    /// </summary>
    private List<Unit> Units(IReadOnlyList<Step> steps)
    {
        var units = new List<Unit>();
        foreach (var step in steps)
        {
            // An annotation after a macro call is about the last statement of its expansion,
            // which is before the step that marks where the expansion ends.
            if (step.Statement is StatementSyntax annotation && Annotations.Is(annotation)
                && units.FindLast(unit => !unit.Step.IsMarker) is { } above)
            {
                above.Annotations.Add(annotation);
                continue;
            }
            units.Add(new Unit(step));
        }
        foreach (var unit in units.Where(unit => unit.Annotations.Count > 0))
            annotations[unit.Step.Key] = unit.Annotations;
        return units;
    }

    /// <summary>
    /// Finds the branches that are calls. Such a branch is <c>brl f</c> or <c>bra f</c> to a
    /// routine, directly after <c>per L-1</c>, where <c>L</c> is the label directly after the
    /// branch. A far call also has a <c>phk</c> directly before the <c>per</c>.
    /// </summary>
    /// <returns>
    /// The call each such branch makes, and the <c>per</c> statements that push the calls' return
    /// addresses.
    /// </returns>
    private (Dictionary<StepKey, RelativeCall> Calls, List<StepKey> ReturnAddresses)
        FindRelativeCalls(IReadOnlyList<Unit> units)
    {
        var calls = new Dictionary<StepKey, RelativeCall>();
        var returnAddresses = new List<StepKey>();
        for (var i = 1; i + 1 < units.Count; i++)
        {
            var branch = units[i].Step;
            var push = units[i - 1];
            if (push.Step.Stream != branch.Stream || units[i + 1].Step.Stream != branch.Stream
                || !IsInstruction(branch.Statement, MnemonicKind.Brl, MnemonicKind.Bra)
                || !IsInstruction(push.Step.Statement, MnemonicKind.Per) || push.Next is not null || units[i].Next is not null
                || units[i + 1].Step.Label is not { } after
                || Targets.Of(model, Transfers.TargetOf(branch.Statement, AddressingMode.Relative), branch.On)
                    is not { Symbol.Signature: not null } routine
                || !NamesTheAddressBefore(push.Step, after))
            {
                continue;
            }
            var far = i >= 2 && units[i - 2].Step.Stream == branch.Stream
                && IsInstruction(units[i - 2].Step.Statement, MnemonicKind.Phk);
            calls[branch.Key] = new RelativeCall(routine.Symbol, push.Step, far ? units[i - 2].Step : null);
            returnAddresses.Add(push.Step.Key);
        }
        return (calls, returnAddresses);
    }

    /// <summary>
    /// Returns whether a <c>per</c> pushes <c>L-1</c>, the byte before <paramref name="label"/>, as
    /// a return address does.
    /// </summary>
    private bool NamesTheAddressBefore(Step push, Symbol label)
    {
        if (push.Statement is not InstructionStatementSyntax { Operand: AbsoluteOperandSyntax { Prefix: null } pushed }
            || pushed.Address is not BinaryExpressionSyntax { OperatorToken.Kind: SyntaxKind.Minus } difference)
        {
            return false;
        }
        return Targets.Of(model, difference.Left, push.On)?.Symbol == label
            && model.ValueOf(difference.Right, push.On).AsNumber() == 1;
    }

    /// <summary>
    /// Returns the blocks of one region. A label starts a block, and a statement that transfers
    /// control ends one, so if any of a block runs, all of it runs.
    /// </summary>
    /// <param name="units">The statements of the region.</param>
    /// <param name="calls">The relative calls among the statements, from <see cref="FindRelativeCalls"/>.</param>
    /// <param name="inlineData">
    /// The data that calls return past, from <see cref="FlowChecks.FindInlineData"/>. The processor
    /// never runs it, so it costs nothing, rather than leaving its block without a count.
    /// </param>
    private List<BasicBlock> Blocks(
        IReadOnlyList<Unit> units, IReadOnlyDictionary<StepKey, RelativeCall> calls, HashSet<Unit> inlineData)
    {
        var skipped = inlineData.Select(unit => unit.Step.Key).ToHashSet();
        var blocks = new List<BasicBlock>();
        var tails = new List<Unit?>();
        var fallenInto = new List<bool>();
        var found = new Dictionary<(Symbol Symbol, Expansion? At), int>();
        var landed = new Dictionary<StepKey, int>();

        // Whether the block being built can still take statements, and whether the one
        // before it ran off its end into whatever comes next.
        var open = false;
        var runsOn = false;

        int? stream = null;
        foreach (var unit in units)
        {
            // Nothing runs from the end of one stream into the start of the next.
            if (unit.Step.Stream != stream)
            {
                open = false;
                runsOn = false;
                stream = unit.Step.Stream;
            }
            if (unit.Step.Label is { } label)
            {
                Open(label, unit.Step.On);
                found.TryAdd((label, Expansion.Owning(unit.Step.On, label)), blocks.Count - 1);
                continue;
            }

            // The bytes from a position inside an instruction are a block of their own, in a
            // stream of their own, entered only at the name the `.label` gives the position.
            if (layout.HiddenPathAt(unit.Step) is { } hidden)
            {
                Open(hidden.Label, unit.Step.On);
                found.TryAdd((hidden.Label, Expansion.Owning(unit.Step.On, hidden.Label)), blocks.Count - 1);
            }

            // Where such bytes reach the start of an instruction, control enters it from them as
            // well as from the statement before it, so a block starts there.
            else if (!open || (layout.Landings.Contains(unit.Step.Key) && blocks[^1].Steps.Count > 0))
            {
                Open(null, unit.Step.On);
            }
            if (layout.Landings.Contains(unit.Step.Key))
                landed.TryAdd(unit.Step.Key, blocks.Count - 1);

            blocks[^1].Add(unit.Step);
            tails[^1] = unit;
            if (!EndsBlock(unit))
                continue;
            open = false;
            runsOn = BasicBlock.Continues(EndOf(unit, RelativeCallIn(calls, unit.Step)));
        }

        Link(blocks, tails, fallenInto, found, landed, calls);
        Reach(blocks);
        for (var i = 0; i < blocks.Count; i++)
        {
            blocks[i].IsFallenInto = fallenInto[i];
            blocks[i].Next = tails[i]?.Next;
            blocks[i].RunsInto = tails[i]?.Step is { Statement: FallthroughDirectiveSyntax { Target: { } into } } end
                ? RoutineNamed(into, end.On)
                : null;
            blocks[i].Cycles = Counted(blocks[i], skipped);
        }
        return blocks;

        void Open(Symbol? label, Expansion? on)
        {
            blocks.Add(new BasicBlock(blocks.Count, label, on, stream!.Value));
            tails.Add(null);
            fallenInto.Add(runsOn);
            open = true;
            runsOn = true;
        }
    }

    /// <summary>
    /// Adds the edges between blocks. Fall-through is already known from how the blocks were cut.
    /// What is left is where each block's last statement says control goes.
    /// </summary>
    private void Link(
        List<BasicBlock> blocks, List<Unit?> tails, List<bool> fallenInto,
        Dictionary<(Symbol Symbol, Expansion? At), int> found, Dictionary<StepKey, int> landed,
        IReadOnlyDictionary<StepKey, RelativeCall> calls)
    {
        for (var i = 0; i < blocks.Count; i++)
        {
            if (i + 1 < blocks.Count && fallenInto[i + 1])
                Edge(i, i + 1, EdgeKind.FallThrough);
            if (tails[i] is not { } tail)
                continue;

            // The bytes from a position inside an instruction go on at the instruction whose start
            // they reach, as a jump there would.
            if (layout.HiddenPathAt(tail.Step) is { } hidden)
            {
                blocks[i].End = BlockEnd.Jump;
                if (landed.TryGetValue(hidden.Landing, out var at))
                    Edge(i, at, EdgeKind.Taken);
                continue;
            }

            var mode = layout.Of(tail.Step.Statement, tail.Step.On)?.Mode;
            var transfer = Transfers.Of(tail.Step.Statement, mode);
            var relative = RelativeCallIn(calls, tail.Step);
            var makesCall = transfer == Transfer.Call || relative is not null;
            blocks[i].End = EndOf(tail, relative);

            // A `.next` replaces the operand as the source of the targets, because the operand
            // does not identify them. On a call it lists the routines called, and the call
            // still returns to the statement after it. That holds for a call through a pointer
            // too, which is where a `.next` is most needed.
            if (tail.Next is { } next)
            {
                // `.next ?` withholds where control goes, so it goes to a routine nothing is known
                // about. A call still comes back, and anything else hands control over for good.
                if (next.QuestionToken is not null)
                {
                    blocks[i].CallsUnknown = true;
                    if (!blocks[i].EndsInCall)
                        blocks[i].End = BlockEnd.TailCall;
                }

                // `.next .return` says the statement goes back to the routine's caller, as the
                // routine's own return would. The labels listed after it are followed as well.
                if (next.ReturnToken is not null && !blocks[i].EndsInCall)
                    blocks[i].End = BlockEnd.Return;
                foreach (var named in Named(next, tail.Step.On))
                {
                    if (found.TryGetValue(named, out var to))
                        Edge(i, to, EdgeKind.Declared);
                    if (makesCall || IsCall(tail.Step.Statement))
                        blocks[i].Called(named.Symbol);
                    else if (!found.ContainsKey(named))
                        blocks[i].Left(named.Symbol);
                }
                continue;
            }

            if (transfer is not (Transfer.Branch or Transfer.Jump or Transfer.Call))
            {
                // A `jsr` whose target the operand does not name is still a call, and one that
                // nothing here can follow into.
                blocks[i].CallsUnknown |= transfer == Transfer.Elsewhere && IsCall(tail.Step.Statement);
                continue;
            }
            var target = Targets.Of(model, Transfers.TargetOf(tail.Step.Statement, mode), tail.Step.On);
            var inside = false;
            if (target is { } resolved && found.TryGetValue(resolved, out var landing))
            {
                inside = true;
                Edge(i, landing, makesCall ? EdgeKind.Call : EdgeKind.Taken);
            }
            blocks[i].BranchesOut = transfer == Transfer.Branch && !inside && relative is null;
            if (blocks[i].BranchesOut && target is { } outside)
                blocks[i].Left(outside.Symbol);

            // What a call reaches costs what that routine costs, and so does what a tail jump
            // reaches, since control comes back from it to this routine's caller. A target
            // inside this routine is neither, because the path simply continues into it.
            if (relative is { } known)
            {
                blocks[i].Called(known.Routine);
            }
            else if (makesCall)
            {
                blocks[i].SetCalled(target?.Symbol);
            }
            else if (transfer == Transfer.Jump && !inside)
            {
                blocks[i].SetCalled(target?.Symbol);
                blocks[i].End = BlockEnd.TailCall;
            }
        }

        void Edge(int from, int to, EdgeKind kind)
        {
            blocks[from].Reach(to, kind);
            blocks[to].ReachedFrom(from);
        }
    }

    /// <summary>
    /// Returns how long the whole block takes. A block runs all of it or none, so the counts add
    /// up. One statement nt65 has no count for leaves the block without one. The data in
    /// <paramref name="skipped"/> is returned past rather than run, so it costs nothing.
    /// </summary>
    private CycleCount? Counted(BasicBlock block, HashSet<StepKey> skipped)
    {
        var total = new CycleCount(0);
        foreach (var step in block.Steps)
        {
            if (TakesNoTime(step) || skipped.Contains(step.Key))
                continue;
            if (layout.Of(step.Statement, step.On)?.Cycles is not { } cycles)
            {
                block.Uncounted = Uncounted(step);
                return null;
            }
            total += cycles;
        }

        // A block with nothing in it is the one a routine opens with when its first line is a
        // label, and running none of it takes no time at all.
        return total;
    }

    /// <summary>
    /// Returns whether a statement is the last of its block, because control leaves after it. A
    /// call leaves too, even though it comes back. What it reaches is an edge of its own, and an
    /// edge leaves a block at its end.
    /// </summary>
    private bool EndsBlock(Unit unit) =>
        unit.Next is not null || unit.Step.Statement is FallthroughDirectiveSyntax or LabelDirectiveSyntax
        || Transfers.Of(unit.Step.Statement, layout.Of(unit.Step.Statement, unit.Step.On)?.Mode)
            != Transfer.Through;

    /// <summary>
    /// Returns the labels a table or a list expands to. The result is empty when the target does
    /// not expand to labels and so names only itself.
    /// </summary>
    /// <summary>
    /// Finds each instruction that stands on a label a <c>.patch</c> names, the variants each
    /// <c>.patch … as</c> lists for it, and which of its bytes the stores may write.
    /// </summary>
    private void FindPatched()
    {
        var targets = new Dictionary<Symbol, List<(PatchDirectiveSyntax Patch, Step Store)>>();
        foreach (var step in layout.Steps)
        {
            foreach (var patch in AnnotationsOf(step).OfType<PatchDirectiveSyntax>())
            {
                foreach (var target in Annotations.TargetsOf(patch))
                {
                    if (Targets.Of(model, target, step.On)?.Symbol is { } symbol)
                    {
                        if (!targets.TryGetValue(symbol, out var patches))
                            targets[symbol] = patches = [];
                        patches.Add((patch, step));
                    }
                }
            }
        }

        // The sets are built in full before any is published, so a thread that finds one set
        // never sees it half built.
        var found = new HashSet<StepKey>();
        var foundVariants = new Dictionary<StepKey, IReadOnlyList<MnemonicKind>>();
        var listed = new List<PatchVariant>();
        var opcodes = new HashSet<StepKey>();
        var operands = new HashSet<StepKey>();
        List<(PatchDirectiveSyntax Patch, Step Store, Symbol Label)>? naming = null;
        foreach (var step in layout.Steps)
        {
            if (step.Label is { } label)
            {
                if (targets.TryGetValue(label, out var patches))
                    (naming ??= []).AddRange(patches.Select(patch => (patch.Patch, patch.Store, label)));
                continue;
            }
            if (naming is not null && step.Statement is InstructionStatementSyntax written)
            {
                found.Add(step.Key);
                var line = layout.Of(written, step.On);
                var kept = new List<MnemonicKind>();
                var opcode = false;
                var operand = false;
                foreach (var (patch, store, named) in naming)
                {
                    // A variant replaces the opcode alone, so it is taken only from a store
                    // known to start at the opcode. Any other store may write anything into the
                    // bytes it reaches.
                    var bytes = WrittenBytes(store, named);
                    var atOpcode = bytes?.First == 0;
                    var variant = false;
                    foreach (var name in patch.Variants)
                    {
                        var listing = new PatchVariant(name, store.On, step, written.MnemonicKind, line?.Mode, layout.Cpu, atOpcode);
                        listed.Add(listing);
                        if (listing.Problem is not null)
                            continue;
                        variant = true;
                        if (!kept.Contains(listing.Mnemonic))
                            kept.Add(listing.Mnemonic);
                    }
                    opcode |= !variant && (bytes is not { } reached || (reached.First <= 0 && reached.Last >= 0));
                    operand |= bytes is not { } into || line is null || (into.Last >= 1 && into.First < line.Length);
                }
                if (opcode)
                    opcodes.Add(step.Key);
                else if (kept.Count > 0)
                    foundVariants[step.Key] = kept;
                if (operand)
                    operands.Add(step.Key);
            }
            naming = null;
        }
        patched = found;
        variants = foundVariants;
        listedVariants = listed;
        rewrittenOpcodes = opcodes;
        rewrittenOperands = operands;
    }

    /// <summary>
    /// Returns the bytes the store at <paramref name="store"/> may write, as offsets from
    /// <paramref name="label"/>, or null where they are not known. They are known only where the
    /// store addresses the label directly, as <c>sta @op+1</c> does. On the 65816 a store sized by
    /// a register is taken to be two bytes wide, because the width is not known yet.
    /// </summary>
    private (long First, long Last)? WrittenBytes(Step store, Symbol label)
    {
        if (store.Statement is not InstructionStatementSyntax instruction
            || layout.Of(instruction, store.On)?.Mode is not (AddressingMode.Direct or AddressingMode.Absolute or AddressingMode.Long)
            || StepOperands.Of(model, store) is not { } operand
            || Location.Of(model, CodeLayout.Expression(operand), store.On) is not { Root: { } root } location
            || root != label)
        {
            return null;
        }
        var width = layout.Cpu == Cpu.Wdc65816 && Instructions.MemorySizedBy(instruction.MnemonicKind) is not null ? 2 : 1;
        return (location.Offset, location.Offset + width - 1);
    }

    private IEnumerable<(Symbol Symbol, Expansion? At)> Spread(Symbol target, Expansion? on)
    {
        var items = target.Kind == SymbolKind.List
            ? target.Items.Select(item => (Item: item, On: on))
            : ItemsOfTable(target);
        foreach (var (item, at) in items)
        {
            if (Targets.Of(model, Stripped(item), at) is { } named
                && (named.Symbol.Kind is SymbolKind.Label || named.Symbol.Signature is not null))
            {
                yield return named;
            }
            else
                yield break;
        }
    }

    /// <summary>
    /// Returns the addresses a table holds, each paired with the <see cref="Expansion"/> it is in.
    /// A table is data that <see cref="IsAddressData"/> accepts. Its addresses are the values of
    /// an <c>.addr</c> or <c>.faraddr</c> declaration, the values of the address members of
    /// records, and those of each member of mixed data in turn. Values in a body are read from the
    /// expansions layout made of them, since each iteration of a repetition there may emit a value
    /// differently. A label on an <c>.addr</c> or <c>.faraddr</c> line is a table too, of the
    /// values on that line. Any other symbol yields no values.
    /// </summary>
    private IEnumerable<(SyntaxNode Item, Expansion? On)> ItemsOfTable(Symbol target)
    {
        if (target.Kind == SymbolKind.Label)
        {
            foreach (var item in ItemsAfterLabel(target))
                yield return item;
            yield break;
        }
        if (!IsAddressData(target))
            yield break;
        if (target.Data is not DataDirectiveSyntax element)
        {
            foreach (var member in target.Body?.Symbols ?? [])
            {
                foreach (var item in ItemsOfTable(member))
                    yield return item;
            }
            yield break;
        }

        var type = element.IsRecord ? target.Type : null;
        foreach (var value in DataLengths.ElementsOf(element))
        {
            foreach (var item in type is null ? [value] : AddressesIn(type, value))
                yield return (item, null);
        }
        if (type is not null && element.Tail is BracedDataSyntax { Value: RecordValuesSyntax record })
        {
            foreach (var item in AddressesIn(type, record))
                yield return (item, null);
        }
        if (DataSyntax.BodyOf(element) is not { } body)
            yield break;

        // A single record's body holds its `member = value` lines; an array's holds records.
        if (type is not null && body.BlockKind == BlockKind.RecordInitializer)
        {
            foreach (var item in AddressesIn(type, body.Members.Skip(1).OfType<LineSyntax>().Select(line => line.Statement)))
                yield return (item, null);
            yield break;
        }
        foreach (var step in layout.Steps)
        {
            if (step.Statement is DataValuesSyntax values && DataSyntax.DirectiveOfValues(values) == element)
            {
                foreach (var value in DataLengths.ElementsOf(values))
                {
                    foreach (var item in type is null ? [value] : AddressesIn(type, value))
                        yield return (item, step.On);
                }
            }
        }
    }

    /// <summary>
    /// Returns the addresses on the <c>.addr</c> or <c>.faraddr</c> line that directly follows a
    /// label, each paired with the <see cref="Expansion"/> it is in. A label on any other line
    /// yields no values.
    /// </summary>
    private IEnumerable<(SyntaxNode Item, Expansion? On)> ItemsAfterLabel(Symbol label)
    {
        var steps = layout.Steps;
        for (var i = 0; i < steps.Count - 1; i++)
        {
            if (steps[i].Label != label)
                continue;
            var next = steps[i + 1];
            if (next.Statement is DataDirectiveSyntax { IsRecord: false } element && IsAddressElement(element))
            {
                foreach (var value in DataLengths.ElementsOf(element))
                    yield return (value, next.On);
            }
            yield break;
        }
    }

    /// <summary>
    /// Returns the values a braced record gives the address members of <paramref name="type"/>.
    /// </summary>
    private static IEnumerable<SyntaxNode> AddressesIn(Symbol type, SyntaxNode record) =>
        AddressesIn(type, record is RecordValuesSyntax values ? values.Members : []);

    /// <summary>
    /// Returns the values that <c>member = value</c> pairs give the address members of
    /// <paramref name="type"/>, in the order the type declares its members. A member that is an
    /// array of addresses gives each item of its list, and a member that is a record, or an array
    /// of them, gives the addresses its own values hold. A member no value names holds zero, which
    /// is no address of code, so it gives nothing.
    /// </summary>
    private static IEnumerable<SyntaxNode> AddressesIn(Symbol type, IEnumerable<StatementSyntax> pairs)
    {
        if (type.IsCyclic)
            yield break;
        var given = new Dictionary<string, SyntaxNode>(StringComparer.Ordinal);
        foreach (var pair in pairs.OfType<MemberValueSyntax>())
            given[pair.Name.Text] = pair.Value;
        foreach (var member in type.Body?.Symbols ?? [])
        {
            if (member.Kind != SymbolKind.Member || given.GetValueOrDefault(member.Name) is not { } value)
                continue;
            var items = value is ValueListSyntax list ? [.. list.Values] : new[] { value };
            foreach (var item in items)
            {
                if (member.Type is { IsLayout: true } inner)
                {
                    foreach (var address in AddressesIn(inner, item))
                        yield return address;
                }
                else if (member.Data is DataDirectiveSyntax element && IsAddressElement(element))
                    yield return item;
            }
        }
    }

    /// <summary>Represents one statement and the annotations under it.</summary>
    internal sealed class Unit(Step step)
    {
        /// <summary>Gets the statement itself.</summary>
        public Step Step { get; } = step;

        /// <summary>Gets the annotations under it, in source order.</summary>
        public List<StatementSyntax> Annotations { get; } = [];

        /// <summary>Gets the <c>.next</c> among them, or null when there is none.</summary>
        public NextDirectiveSyntax? Next => Annotations.OfType<NextDirectiveSyntax>().FirstOrDefault();
    }
}
