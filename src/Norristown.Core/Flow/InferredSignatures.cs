using System.Runtime.CompilerServices;
using Norristown.Layout;
using Norristown.Processor;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// Holds the signature every routine of the program is analyzed with: what it declares, and what
/// is inferred for the rest. A routine that no path returns from never returns, on every
/// processor. On the 65816, each part of the state a routine does not declare, as
/// <see cref="Signature.Declared"/> tells, is inferred too.
/// <list type="bullet">
/// <item>
/// <description>
/// The exit comes from the routine's body. A part is unchanged where every return hands back
/// what the routine was entered with, one value where every return agrees on it, and unknown
/// otherwise.
/// </description>
/// </item>
/// <item>
/// <description>
/// The entry comes from what the routine's callers in the program agree on, in any module. Code
/// outside nt65 that calls an exported routine is not checked, as nothing outside nt65 is, and a
/// routine it calls declares its entry where the two have to agree. A routine whose address is
/// taken, and one that nothing calls, keep the default entry, because some of their callers
/// cannot be seen.
/// </description>
/// </item>
/// </list>
/// <para>
/// The parts fall into two classes. The widths and the mode decide how the routine's bytes are
/// read, so callers that disagree on a width the body depends on are a mistake, which the
/// analysis reports at the routine. The direct page and the data bank decide only which memory
/// an operand reaches, and a routine may be meant to run with several. Callers that disagree on
/// them combine as two paths do where they meet: the data bank becomes one of the callers' banks
/// and the direct page becomes unknown.
/// </para>
/// <para>
/// The answers are solved by running each file's processor-state analysis with the signatures
/// as they stand, and learning from the calls and returns it records, until no file took a
/// signature that has since changed. Every answer starts at nothing known and only moves one
/// way. An entry's part goes from no caller seen, to the value the callers agree on, to
/// disagreeing or unknown. An exit goes from no return seen, to a value, to unknown. A call to a
/// routine none of whose returns has been seen yet ends its path for now, as a call to one that
/// never returns does. Each part of each routine can change at most twice, so the rounds end.
/// </para>
/// </summary>
public sealed class InferredSignatures
{
    // How many earlier analyses are kept for each layout. Solving takes a handful of rounds, and
    // each round gives a file at most one new set of signatures.
    private const int SolvedKept = 8;

    // The processor-state analyses that solving has run on each file's layout, kept across rounds
    // and across settles. A file an edit did not touch keeps its layout and its model, and solving
    // gives it the same signatures round by round each time, so its analyses are found here.
    private static readonly ConditionalWeakTable<CodeLayout, List<Solved>> solvedOn = new();

    // The routines each file names as where control goes, and those whose address it takes, by
    // the file's model, which is kept across edits that do not touch the file.
    private static readonly ConditionalWeakTable<SemanticModel, Uses> usesIn = new();

    private readonly Dictionary<RoutineKey, Signature> found;
    private readonly HashSet<RoutineKey> unreturned;
    private readonly HashSet<RoutineKey> unsettled;
    private readonly Dictionary<(RoutineKey Routine, StateParts Part), IReadOnlyList<(string State, Span At)>> disagreements;

    private InferredSignatures(
        Dictionary<RoutineKey, Signature> found, HashSet<RoutineKey> unreturned, HashSet<RoutineKey> unsettled,
        Dictionary<(RoutineKey, StateParts), IReadOnlyList<(string State, Span At)>> disagreements)
    {
        this.found = found;
        this.unreturned = unreturned;
        this.unsettled = unsettled;
        this.disagreements = disagreements;
    }

    /// <summary>
    /// Gets signatures in which every routine has only what it declares, for an analysis that runs
    /// before the program's signatures are inferred.
    /// </summary>
    public static InferredSignatures None { get; } = new([], [], [], []);

    /// <summary>
    /// Returns the signature <paramref name="routine"/> is analyzed with, or null for a symbol
    /// that is not a routine. A routine with no body in the program that declares nothing about
    /// its state returns with every part unknown, because nothing says what it does.
    /// </summary>
    public Signature? Of(Symbol routine)
    {
        if (routine.Signature is not { } declared)
            return null;
        if (found.TryGetValue(RoutineKey.Of(routine), out var inferred))
            return inferred;
        return routine.Kind is SymbolKind.ExternProc or SymbolKind.ImportedAddress && !declared.DeclaresState && !declared.IsInterrupt
            ? declared with { Exit = ProcessorState.Unknown }
            : declared;
    }

    /// <summary>
    /// Returns a value indicating whether what <paramref name="routine"/> returns with is worked
    /// out. It is not while the signatures are being solved and no return of the routine has
    /// been seen, and a path that calls the routine ends there until one is.
    /// </summary>
    public bool IsExitKnown(Symbol routine) => !unreturned.Contains(RoutineKey.Of(routine));

    /// <summary>
    /// Returns a value indicating whether <paramref name="routine"/>'s entry is worked out. It is
    /// not while the signatures are being solved and its callers have not all given what it is
    /// entered with, and what its body does is not learned from until it is.
    /// </summary>
    public bool IsEntrySettled(Symbol routine) => !unsettled.Contains(RoutineKey.Of(routine));

    /// <summary>
    /// Returns the callers that disagree on <paramref name="part"/> of <paramref name="routine"/>'s
    /// entry, each with the state it calls in and where, or null where they agree.
    /// </summary>
    public IReadOnlyList<(string State, Span At)>? DisagreementOn(Symbol routine, StateParts part) =>
        disagreements.GetValueOrDefault((RoutineKey.Of(routine), part));

    /// <summary>
    /// Works out the signature of every routine of <paramref name="files"/>. Which routines never
    /// return comes from <paramref name="effects"/>. On the 65816, each file's processor-state
    /// analysis is run again with the signatures as they are learned, until none changes.
    /// </summary>
    /// <param name="files">The program's files, each analyzed on its own.</param>
    /// <param name="effects">What each routine leaves on its caller's stack.</param>
    /// <param name="ranges">The project's table of which banks each range of addresses is reached from.</param>
    /// <param name="cancellation">The token checked before each round.</param>
    internal static InferredSignatures Solve(
        IReadOnlyList<FileAnalysis> files, StackEffects effects, IReadOnlyList<Project.AccessRange> ranges,
        CancellationToken cancellation)
    {
        var regions = new Dictionary<RoutineKey, FlowRegion>();
        foreach (var file in files)
        {
            foreach (var region in file.Flow.Regions)
            {
                if (region is { IsEntered: true, Blocks.Count: > 0, Routine.Signature: { IsInterrupt: false } })
                    regions.TryAdd(RoutineKey.Of(region.Routine), region);
            }
        }
        // A routine that hands control to an interrupt handler is left by the handler's `rti`,
        // which goes somewhere, so it is not taken never to return.
        var never = regions
            .Where(pair => !pair.Value.Routine.Signature!.NeverReturns
                && effects.Of(pair.Value.Routine).Kind == StackEffectKind.NeverReturns
                && !pair.Value.Blocks.Any(block => block.Calls.Append(block.RunsInto).Any(target => target?.Signature is { IsInterrupt: true })))
            .Select(pair => pair.Key)
            .ToHashSet();

        var states = files.Where(file => file.State is not null).ToDictionary(file => file, file => file.State!);
        if (states.Count == 0)
        {
            var returning = regions.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Routine.Signature! with { NeverReturns = pair.Value.Routine.Signature!.NeverReturns || never.Contains(pair.Key) });
            return new InferredSignatures(returning, [], [], []);
        }

        var called = new HashSet<RoutineKey>();
        var taken = new HashSet<RoutineKey>();
        foreach (var file in files)
        {
            var uses = usesIn.GetValue(file.Model, Uses.Of);
            called.UnionWith(uses.Called);
            taken.UnionWith(uses.Taken);
        }
        var solver = new Solver(regions, never, called, taken);
        var signatures = solver.Signatures();
        while (true)
        {
            var stale = states.Where(pair => pair.Value.TookStale(signatures)).Select(pair => pair.Key).ToList();
            if (stale.Count == 0)
            {
                // Once nothing more can be learned, an entry no caller has given a value, as in a
                // cycle of routines that nothing outside calls, takes its default. Then an exit
                // no return has been seen for, as in a routine that only returns past a call to
                // itself, is unknown, unless the routine never returns at all.
                if (!solver.Finish())
                    return signatures;
                signatures = solver.Signatures();
                continue;
            }
            var analyzed = new StateAnalysis[stale.Count];
            ParallelWork.For(stale.Count, i => analyzed[i] = StateOf(stale[i], ranges, effects, signatures), cancellation);
            for (var i = 0; i < stale.Count; i++)
                states[stale[i]] = analyzed[i];
            solver.Learn(states.Values);
            signatures = solver.Signatures();
        }
    }

    /// <summary>
    /// Returns the exit of a routine entered with <paramref name="entry"/> whose returns leave
    /// <paramref name="returned"/>, for the <paramref name="undeclared"/> parts. A part that is
    /// what the routine was entered with is unchanged, so that it follows the caller.
    /// </summary>
    private static ProcessorState Exit(ProcessorState declared, ProcessorState entry, ProcessorState returned, StateParts undeclared)
    {
        return new(
            (undeclared & StateParts.A) != 0 ? Width(entry.A, returned.A) : declared.A,
            (undeclared & StateParts.Index) != 0 ? Width(entry.Index, returned.Index) : declared.Index,
            (undeclared & StateParts.Mode) != 0 ? (returned.E == entry.E ? ProcessorMode.Unchanged : returned.E) : declared.E,
            (undeclared & StateParts.DirectPage) != 0 ? Value(entry.D, returned.D) : declared.D,
            (undeclared & StateParts.DataBank) != 0 ? Value(entry.B, returned.B) : declared.B);

        static Width Width(Width entered, Width left) => left == entered ? Semantics.Width.Unchanged : left;

        static StateValue Value(StateValue entered, StateValue left) =>
            left.IsEntered || left == entered ? StateValue.Unchanged : left;
    }

    /// <summary>Returns what two exits a routine was found to return with agree on.</summary>
    private static ProcessorState Joined(ProcessorState a, ProcessorState b) => new(
        a.A == b.A ? a.A : Width.Unknown,
        a.Index == b.Index ? a.Index : Width.Unknown,
        a.E == b.E ? a.E : ProcessorMode.Unknown,
        StateValue.Merge(a.D, b.D),
        StateValue.Merge(a.B, b.B));

    /// <summary>Returns the single parts of <paramref name="parts"/>.</summary>
    private static IEnumerable<StateParts> Parts(StateParts parts) =>
        new[] { StateParts.A, StateParts.Index, StateParts.Mode, StateParts.DirectPage, StateParts.DataBank }
            .Where(part => (parts & part) != 0);

    /// <summary>Returns <paramref name="entry"/> with <paramref name="parts"/> taken from <paramref name="defaults"/>.</summary>
    private static ProcessorState Defaulted(ProcessorState entry, StateParts parts, ProcessorState defaults) => new(
        (parts & StateParts.A) != 0 ? defaults.A : entry.A,
        (parts & StateParts.Index) != 0 ? defaults.Index : entry.Index,
        (parts & StateParts.Mode) != 0 ? defaults.E : entry.E,
        (parts & StateParts.DirectPage) != 0 ? defaults.D : entry.D,
        (parts & StateParts.DataBank) != 0 ? defaults.B : entry.B);

    /// <summary>
    /// Returns <paramref name="entry"/> with <paramref name="part"/> set from what the callers
    /// agree on. Where they do not, a width or the mode keeps its default, and the direct page
    /// becomes unknown, as it does where two paths meet. Where the callers agree on emulation
    /// mode, the widths the routine does not declare are 8 bits, which is all they can be.
    /// </summary>
    private static ProcessorState With(ProcessorState entry, StateParts part, Seen answer, ProcessorState defaults)
    {
        if (answer.Kind == SeenKind.Unknown || (answer.Kind == SeenKind.Disagree && part != StateParts.DirectPage))
            return Defaulted(entry, part, defaults);
        return part switch
        {
            StateParts.A => entry with { A = answer.Value == 0 ? Width.Eight : Width.Sixteen },
            StateParts.Index => entry with { Index = answer.Value == 0 ? Width.Eight : Width.Sixteen },
            StateParts.Mode when answer.Value == 0 => entry with { E = ProcessorMode.Native },
            StateParts.Mode => entry with
            {
                E = ProcessorMode.Emulation,
                A = entry.A == Width.Unchanged ? Width.Eight : entry.A,
                Index = entry.Index == Width.Unchanged ? Width.Eight : entry.Index,
            },
            StateParts.DirectPage => entry with
            {
                D = answer.Kind == SeenKind.Disagree ? StateValue.Unknown : StateValue.Of(answer.Value),
            },
            _ => entry with { B = StateValue.Within(answer.Banks) },
        };
    }

    /// <summary>Returns a value a caller gives, as a signature item writes it.</summary>
    private static string Format(StateParts part, Seen seen) => part switch
    {
        StateParts.A => ProcessorState.Format(StateRegister.A, seen.Value == 0 ? Width.Eight : Width.Sixteen),
        StateParts.Index => ProcessorState.Format(StateRegister.Index, seen.Value == 0 ? Width.Eight : Width.Sixteen),
        _ => StateValue.Of(seen.Value).Format(StateRegister.DirectPage),
    };

    /// <summary>
    /// Returns the processor-state analysis of <paramref name="file"/> with
    /// <paramref name="signatures"/> and <paramref name="effects"/>. An analysis that an earlier
    /// round or settle ran on the same layout, model and blocks is reused when every signature
    /// and stack effect it took is what they give now, since it would come out the same.
    /// </summary>
    private static StateAnalysis StateOf(
        FileAnalysis file, IReadOnlyList<Project.AccessRange> ranges, StackEffects effects, InferredSignatures signatures)
    {
        var solved = solvedOn.GetOrCreateValue(file.Layout);
        lock (solved)
        {
            foreach (var earlier in solved)
            {
                if (earlier.IsFor(file, ranges) && !earlier.State.TookStale(signatures)
                    && earlier.State.Consumed.All(taken => effects.Of(taken.Key) == taken.Value))
                {
                    return earlier.State;
                }
            }
        }
        var state = StateAnalysis.Of(file.Model, file.Layout, file.Flow, ranges, effects, signatures);
        lock (solved)
        {
            if (solved.Count == SolvedKept)
                solved.RemoveAt(0);
            solved.Add(new Solved(file.Model, [.. file.Flow.Regions.Select(region => region.Blocks)], ranges, state));
        }
        return state;
    }

    /// <summary>
    /// Learns the program's signatures from the processor-state analyses of its files, keeping
    /// what has been learned so far between rounds.
    /// </summary>
    private sealed class Solver
    {
        private readonly Dictionary<RoutineKey, FlowRegion> regions;
        private readonly HashSet<RoutineKey> never;

        // The entry each routine is analyzed with, and the parts of it whose callers have given
        // no value yet.
        private readonly Dictionary<RoutineKey, ProcessorState> entries = [];
        private readonly Dictionary<RoutineKey, StateParts> pending = [];

        // What the callers of each routine agree on for each part so far.
        private readonly Dictionary<(RoutineKey, StateParts), Seen> answers = [];

        // What each routine's returns agree on so far. A routine with a part to infer and no
        // entry here has had no return seen.
        private readonly Dictionary<RoutineKey, ProcessorState> exits = [];

        private Dictionary<(RoutineKey, StateParts), IReadOnlyList<(string State, Span At)>> disagreements = [];

        public Solver(
            Dictionary<RoutineKey, FlowRegion> regions, HashSet<RoutineKey> never, HashSet<RoutineKey> called, HashSet<RoutineKey> taken)
        {
            this.regions = regions;
            this.never = never;
            foreach (var (key, region) in regions)
            {
                var routine = region.Routine;
                var declared = routine.Signature!;
                entries[key] = declared.Entry;
                if (called.Contains(key) && !taken.Contains(key))
                    pending[key] = StateParts.All & ~declared.Declared;
            }
        }

        /// <summary>Returns the signatures as they stand.</summary>
        public InferredSignatures Signatures()
        {
            var found = new Dictionary<RoutineKey, Signature>();
            var unreturned = new HashSet<RoutineKey>();
            foreach (var (key, region) in regions)
            {
                var declared = region.Routine.Signature!;
                var inferred = exits.TryGetValue(key, out var exit);
                if (!inferred && declared.Declared != StateParts.All)
                    unreturned.Add(key);
                found[key] = declared with
                {
                    Entry = entries[key],
                    Exit = inferred ? exit : declared.Exit,
                    NeverReturns = declared.NeverReturns || never.Contains(key),
                };
            }
            var unsettled = pending.Where(pair => pair.Value != StateParts.None).Select(pair => pair.Key).ToHashSet();
            return new InferredSignatures(found, unreturned, unsettled, disagreements);
        }

        /// <summary>
        /// Learns from the calls and returns that <paramref name="states"/> recorded, each of
        /// which was analyzed with the signatures as they stand. What a routine whose entry is
        /// still pending does is not learned from, since its body was analyzed with a placeholder.
        /// </summary>
        public void Learn(IEnumerable<StateAnalysis> states)
        {
            var calls = new Dictionary<RoutineKey, List<CallState>>();
            foreach (var state in states)
            {
                foreach (var call in state.Calls)
                {
                    var caller = RoutineKey.Of(call.Caller);
                    var target = RoutineKey.Of(call.Target);
                    if (caller == target || pending.GetValueOrDefault(caller) != StateParts.None || !pending.ContainsKey(target))
                        continue;
                    if (!calls.TryGetValue(target, out var list))
                        calls[target] = list = [];
                    list.Add(call);
                }
                foreach (var (routine, left) in state.Left)
                {
                    var key = RoutineKey.Of(routine);
                    if (pending.GetValueOrDefault(key) != StateParts.None || !regions.TryGetValue(key, out var region))
                        continue;
                    var declared = region.Routine.Signature!;
                    var exit = Exit(declared.Exit, entries[key], left, StateParts.All & ~declared.Declared);
                    exits[key] = exits.TryGetValue(key, out var earlier) ? Joined(earlier, exit) : exit;
                }
            }

            disagreements = [];
            foreach (var (key, made) in calls)
            {
                var declared = regions[key].Routine.Signature!;
                foreach (var part in Parts(StateParts.All & ~declared.Declared))
                {
                    var seen = made.Select(call => (Seen: Resolved(call, part), call.At)).Where(each => each.Seen.Kind != SeenKind.Pending).ToList();
                    if (seen.Count == 0)
                        continue;
                    var answer = seen.Select(each => each.Seen).Aggregate((a, b) => Seen.Join(a, b, part));
                    if (answers.TryGetValue((key, part), out var earlier))
                        answer = Seen.Join(earlier, answer, part);
                    answers[(key, part)] = answer;
                    pending[key] &= ~part;
                    entries[key] = With(entries[key], part, answer, declared.Entry);
                    if (answer.Kind == SeenKind.Disagree && part is StateParts.A or StateParts.Index or StateParts.DirectPage)
                    {
                        disagreements[(key, part)] = [.. seen
                            .Where(each => each.Seen.Kind == SeenKind.Known)
                            .Select(each => (Format(part, each.Seen), each.At))];
                    }
                }
            }
        }

        /// <summary>
        /// Gives every entry part still pending its default, or where there is none, every exit
        /// still unseen of a routine that returns an unknown value for each part it does not
        /// declare. Returns whether there was anything to give.
        /// </summary>
        public bool Finish()
        {
            var any = false;
            foreach (var (key, parts) in pending.Where(pair => pair.Value != StateParts.None).ToList())
            {
                entries[key] = Defaulted(entries[key], parts, regions[key].Routine.Signature!.Entry);
                foreach (var part in Parts(parts))
                    answers[(key, part)] = Seen.Unknown;
                pending[key] = StateParts.None;
                any = true;
            }
            if (any)
                return true;
            foreach (var (key, region) in regions)
            {
                var declared = region.Routine.Signature!;
                if (exits.ContainsKey(key) || never.Contains(key) || declared.NeverReturns || declared.Declared == StateParts.All)
                    continue;
                exits[key] = Defaulted(declared.Exit, StateParts.All & ~declared.Declared, ProcessorState.Unknown);
                any = true;
            }
            return any;
        }

        /// <summary>
        /// Returns what <paramref name="call"/> says about <paramref name="part"/>. A part the
        /// caller leaves as it was entered with is what the caller's own entry has.
        /// </summary>
        private Seen Resolved(CallState call, StateParts part)
        {
            var seen = Seen.Of(call.State, part);
            if (seen.Kind != SeenKind.Entered)
                return seen;
            var entered = entries.TryGetValue(RoutineKey.Of(call.Caller), out var known)
                ? Seen.Of(known, part)
                : Seen.Of(call.Caller.Signature?.Entry ?? ProcessorState.Default, part);
            return entered.Kind == SeenKind.Entered ? Seen.Unknown : entered;
        }
    }

    /// <summary>
    /// Represents a processor-state analysis that solving ran, with the inputs that are not
    /// recorded in the analysis itself.
    /// </summary>
    /// <param name="Model">The model of the file analyzed.</param>
    /// <param name="Blocks">The blocks of each region of the file's control flow, in order.</param>
    /// <param name="Ranges">The table of which banks each range of addresses is reached from.</param>
    /// <param name="State">The analysis.</param>
    private sealed record Solved(
        SemanticModel Model, IReadOnlyList<IReadOnlyList<BasicBlock>> Blocks, IReadOnlyList<Project.AccessRange> Ranges,
        StateAnalysis State)
    {
        /// <summary>
        /// Determines whether the analysis was run on <paramref name="file"/>'s model and blocks,
        /// with <paramref name="ranges"/>. A control flow copied for composing shares its blocks
        /// with the flow it was copied from.
        /// </summary>
        public bool IsFor(FileAnalysis file, IReadOnlyList<Project.AccessRange> ranges)
        {
            var regions = file.Flow.Regions;
            if (!ReferenceEquals(Model, file.Model) || !ReferenceEquals(Ranges, ranges) || regions.Count != Blocks.Count)
                return false;
            for (var i = 0; i < Blocks.Count; i++)
            {
                if (!ReferenceEquals(Blocks[i], regions[i].Blocks))
                    return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Represents the routines one file names as where control goes, and those whose address it
    /// takes. Any use of a routine's name other than as where a call, a jump, a branch, a
    /// <c>.next</c> or a <c>.fallthrough</c> goes takes its address, as <c>.addr</c>, <c>pea</c>
    /// and <c>#&lt;</c> do, and the program may hand control to such a routine from anywhere. An
    /// <c>.export</c> that names a routine does neither: callers outside nt65 are the author's to
    /// answer for.
    /// </summary>
    /// <param name="Called">The routines named as where control goes.</param>
    /// <param name="Taken">The routines whose address is taken.</param>
    private sealed record Uses(HashSet<RoutineKey> Called, HashSet<RoutineKey> Taken)
    {
        /// <summary>Returns the routines <paramref name="model"/>'s file uses.</summary>
        public static Uses Of(SemanticModel model)
        {
            var uses = new Uses([], []);
            foreach (var reference in model.References)
            {
                if (reference is not { IsDeclaration: false, InUse: false, IsStep: false, Symbol: { Signature: not null } routine })
                    continue;
                switch (UseAt(model.Tree, reference.Span))
                {
                    case Use.Transfer:
                        uses.Called.Add(RoutineKey.Of(routine));
                        break;
                    case Use.Address:
                        uses.Taken.Add(RoutineKey.Of(routine));
                        break;
                    default:
                        break;
                }
            }
            return uses;
        }

        /// <summary>Returns how the name at <paramref name="span"/> uses the routine it names.</summary>
        private static Use UseAt(SyntaxTree tree, TextSpan span)
        {
            foreach (var node in tree.Root.FindToken(span.Start).Parent?.AncestorsAndSelf() ?? [])
            {
                switch (node)
                {
                    case ImmediateOperandSyntax:
                        return Use.Address;
                    case NextDirectiveSyntax or FallthroughDirectiveSyntax:
                        return Use.Transfer;
                    case ExportDirectiveSyntax or ExportItemsSyntax:
                        return Use.Export;
                    case InstructionStatementSyntax instruction:
                        return Instructions.IsControlTransfer(instruction.MnemonicKind) ? Use.Transfer : Use.Address;
                    case StatementSyntax:
                        return Use.Address;
                }
            }
            return Use.Address;
        }
    }

    /// <summary>Specifies how a file uses a routine's name.</summary>
    private enum Use
    {
        /// <summary>As where control goes.</summary>
        Transfer,

        /// <summary>For its address, which control may reach from anywhere.</summary>
        Address,

        /// <summary>To export it.</summary>
        Export,
    }

    /// <summary>Specifies what a call says about one part of the state.</summary>
    private enum SeenKind
    {
        /// <summary>Nothing yet.</summary>
        Pending,

        /// <summary>A value, or for the data bank a set of banks.</summary>
        Known,

        /// <summary>What the caller was entered with, which its own entry says.</summary>
        Entered,

        /// <summary>Calls that give different values.</summary>
        Disagree,

        /// <summary>Nothing that can be known.</summary>
        Unknown,
    }

    /// <summary>
    /// Represents what the calls to a routine say about one part of its entry. A width is 0 for 8
    /// bits and 1 for 16, and the mode is 0 for native and 1 for emulation.
    /// </summary>
    /// <param name="Kind">What is known.</param>
    /// <param name="Value">The value, where one is known.</param>
    /// <param name="Banks">The banks, for the data bank.</param>
    private readonly record struct Seen(SeenKind Kind, long Value, BankSet Banks)
    {
        /// <summary>Gets what says nothing that can be known.</summary>
        public static Seen Unknown => new(SeenKind.Unknown, 0, default);

        /// <summary>Returns what <paramref name="state"/> says about <paramref name="part"/>.</summary>
        public static Seen Of(ProcessorState state, StateParts part) => part switch
        {
            StateParts.A => OfWidth(state.A),
            StateParts.Index => OfWidth(state.Index),
            StateParts.Mode => state.E switch
            {
                ProcessorMode.Native => new(SeenKind.Known, 0, default),
                ProcessorMode.Emulation => new(SeenKind.Known, 1, default),
                ProcessorMode.Unchanged => new(SeenKind.Entered, 0, default),
                _ => Unknown,
            },
            StateParts.DirectPage => state.D.Kind switch
            {
                StateValueKind.Known => new(SeenKind.Known, state.D.Value, default),
                StateValueKind.Unchanged => new(SeenKind.Entered, 0, default),
                _ => Unknown,
            },
            _ => state.B.IsBounded
                ? new(SeenKind.Known, 0, state.B.Values.Aggregate(default(BankSet), (set, bank) => set.With(bank, bank)))
                : state.B.Kind == StateValueKind.Unchanged ? new(SeenKind.Entered, 0, default) : Unknown,
        };

        /// <summary>
        /// Returns what two answers agree on. Two sets of banks agree on both, and two other
        /// values that differ disagree. Nothing known outweighs anything.
        /// </summary>
        public static Seen Join(Seen a, Seen b, StateParts part)
        {
            if (a.Kind == SeenKind.Unknown || b.Kind == SeenKind.Unknown)
                return Unknown;
            if (part == StateParts.DataBank)
                return new(SeenKind.Known, 0, a.Banks.Union(b.Banks));
            return a == b ? a : new(SeenKind.Disagree, 0, default);
        }

        private static Seen OfWidth(Width width) => width switch
        {
            Width.Eight => new(SeenKind.Known, 0, default),
            Width.Sixteen => new(SeenKind.Known, 1, default),
            Width.Unchanged => new(SeenKind.Entered, 0, default),
            _ => Unknown,
        };
    }
}
