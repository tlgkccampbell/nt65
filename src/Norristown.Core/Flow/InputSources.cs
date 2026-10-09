using Norristown.Layout;
using Norristown.Processor;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// Represents where each value that the instruction at the caret reads was set, for an editor to
/// show. Each thing the instruction reads is an <em>input</em>. On a call, the inputs are what the
/// routine it calls reads, so the answer shows what is being passed to that routine.
/// <para>
/// A <em>source</em> is a place where an input's value was set, on a path that reaches the caret. A
/// <em>through line</em> is a line the value passed through unchanged on its way, such as a call
/// that keeps the register or the <c>pla</c> that restores a value pushed earlier. The
/// <em>scope</em> is the routine that holds the caret.
/// </para>
/// <para>
/// The answer is only for showing. Nothing warns or errors because of it, which is what lets the
/// sources of a value in memory be a best guess.
/// </para>
/// </summary>
/// <param name="Routine">The span of the routine's name, on the line that opens the routine.</param>
/// <param name="Inputs">Each input of the instruction, in a fixed order: registers, then flags, then widths.</param>
public sealed record InputSources(TextSpan Routine, IReadOnlyList<SourcedInput> Inputs)
{
    /// <summary>
    /// Returns where each input of the instruction on the line at <paramref name="position"/> in
    /// <paramref name="model"/>'s file was set. It returns null where the line holds no instruction,
    /// where the line is inside a macro's definition, whose expansions would each give a different
    /// answer, and where the analysis reached no answer.
    /// </summary>
    public static InputSources? At(ProgramAnalysis analysis, SemanticModel model, int position) =>
        Find(analysis, model, position, held: false);

    /// <summary>
    /// Returns where the value each register and each flag holds just before the statement on the
    /// line at <paramref name="position"/> in <paramref name="model"/>'s file was set, whether or
    /// not the statement reads it. The inputs are A, X and Y and then the flags C, Z, N and V, and
    /// nothing in memory. It returns null where <see cref="At"/> would, except that the statement
    /// may be any that runs.
    /// </summary>
    public static InputSources? Held(ProgramAnalysis analysis, SemanticModel model, int position) =>
        Find(analysis, model, position, held: true);

    /// <summary>
    /// Returns each input of the step at <paramref name="index"/> in <paramref name="block"/>, with an
    /// order key that sorts registers before flags. On a call, or a jump that hands control to a
    /// routine, the inputs are what that routine reads. Where it may read anything, as a routine
    /// with no body here and no <c>reads</c> may, every register is an input.
    /// </summary>
    internal static IEnumerable<(int Order, string Name, InputCategory Category, SourceValue Value)> InputsOf(
        SourceWalk walk, BasicBlock block, int index, SourceState state, Func<Symbol, RoutineReads> readsOf)
    {
        var step = block.Steps[index];
        if (index == block.Steps.Count - 1 && RegisterWalk.CallsAtEnd(block))
        {
            var callees = block.CallsUnknown ? [null] : block.Calls.Cast<Symbol?>().DefaultIfEmpty();
            foreach (var callee in callees)
            {
                var read = callee is null ? Registers.All : readsOf(callee).Assumed;
                foreach (var register in RegisterEffects.Each(read))
                    yield return Register(register, SourceWalk.Given(callee, register, state));

                // On the 65816 a routine reads the widths it declares on entry, which the caller
                // has to have set. Each is named as a signature spells it, such as `a8` or `i16`,
                // so the name says what the routine needs as well as which width it is.
                if (walk.HasWidths && callee?.Signature?.Entry is { } entry)
                {
                    if (entry.A is Semantics.Width.Eight or Semantics.Width.Sixteen)
                        yield return ((int)Tracked.M, $"a{Bits(entry.A)}", InputCategory.Width, state.Of(Tracked.M));
                    if (entry.Index is Semantics.Width.Eight or Semantics.Width.Sixteen)
                        yield return ((int)Tracked.Index, $"i{Bits(entry.Index)}", InputCategory.Width, state.Of(Tracked.Index));
                }
            }
            yield break;
        }

        var statement = (InstructionStatementSyntax)step.Statement;
        foreach (var register in RegisterEffects.Each(ReadBy(walk, step, statement)))
            yield return Register(register, walk.Read(step, register, state));
    }

    /// <summary>
    /// Returns the inputs of the statement on the line at <paramref name="position"/>. With
    /// <paramref name="held"/>, the inputs are every register and flag, as <see cref="Held"/>
    /// says. Without it, they are what the instruction reads, as <see cref="At"/> says.
    /// </summary>
    private static InputSources? Find(ProgramAnalysis analysis, SemanticModel model, int position, bool held)
    {
        var tree = model.Tree;
        if (position >= tree.Text.Length
            || tree.GetLine(tree.GetLineIndex(position)).Statement is not { } statement
            || (!held && statement is not InstructionStatementSyntax)
            || statement.Ancestors().OfType<BlockSyntax>().Any(block => block.Opener.Statement is MacroDeclarationSyntax)
            || analysis.FileFor(tree.Path) is not { } file
            || file.Flow.KeepsOf is not { } keepsOf
            || file.Flow.ReadsOf is not { } readsOf)
        {
            return null;
        }

        foreach (var region in file.Flow.Regions)
        {
            var occurrences = (
                from block in region.Blocks
                from index in Enumerable.Range(0, block.Steps.Count)
                let step = block.Steps[index]
                where !step.Closes && step.Statement.Tree == tree && step.Statement.Position == statement.Position
                select (Block: block, Index: index)).ToList();
            if (occurrences.Count == 0)
                continue;

            var walk = new SourceWalk(model, file.Layout, file.Flow, file.State, keepsOf);
            var reached = walk.Solve(region);

            // A line a repetition unrolls is in the routine once per iteration. Each copy brings its
            // own sources, and the line shows all of them.
            var found = new SortedDictionary<int, (string Name, InputCategory Category, SourceValue Value)>();
            foreach (var (block, index) in occurrences)
            {
                if (reached[block.Index] is not { } entered)
                    continue;
                var state = walk.Before(block, entered, index);
                var inputs = held ? Holding(file, block.Steps[index], state) : InputsOf(walk, block, index, state, readsOf);
                foreach (var (order, name, category, value) in inputs)
                {
                    found[order] = found.TryGetValue(order, out var known)
                        ? (Joined(known.Name, name), category, SourceValue.Merge(known.Value, value))
                        : (name, category, value);
                }
            }
            if (!occurrences.Any(occurrence => reached[occurrence.Block.Index] is not null))
                return null;
            var mapping = new Mapping(tree, region, keepsOf);
            return new InputSources(
                mapping.Routine,
                [
                    .. found.Values.Select(input => mapping.Input(input.Name, input.Category, input.Value)),
                    .. (held ? [] : Memory(analysis, file, region, occurrences))
                        .Select(input => mapping.Memory(input.Key, input.Value)),
                ]);
        }
        return null;
    }

    /// <summary>
    /// Returns where each location in memory that the instruction at the caret reads was set, in the
    /// order of the locations' names. On a call, the locations are the ones the routine called reads
    /// before writing, which <see cref="MemoryInference"/> works out. On any other instruction, they
    /// are each byte its direct operand reads, which is two on the 65816 for a 16-bit access, or the
    /// bytes of the pointer its indirect operand reads.
    /// </summary>
    private static SortedDictionary<Location, MemoryWalk.Value> Memory(
        ProgramAnalysis analysis, FileAnalysis file, FlowRegion region, IReadOnlyList<(BasicBlock Block, int Index)> occurrences)
    {
        var found = new SortedDictionary<Location, MemoryWalk.Value>(
            Comparer<Location>.Create((a, b) => string.CompareOrdinal(a.Name, b.Name) is var order and not 0
                ? order
                : a.GetHashCode().CompareTo(b.GetHashCode())));
        var inference = new MemoryInference(analysis);
        var wanted = new List<(BasicBlock Block, int Index, IReadOnlyList<Location> Locations)>();
        foreach (var (block, index) in occurrences)
        {
            IReadOnlyList<Location> locations = index == block.Steps.Count - 1 && RegisterWalk.CallsAtEnd(block)
                ? [.. block.Calls.SelectMany(inference.ReadsOf).Distinct()]
                : MemoryAccess.Of(file, block.Steps[index]) is { } access
                    ? [.. access.Reads ? access.DirectBytes : [], .. access.Pointer]
                    : [];
            wanted.Add((block, index, locations));
        }
        if (wanted.All(each => each.Locations.Count == 0))
            return found;

        var walk = new MemoryWalk(file, inference, wanted.SelectMany(each => each.Locations));
        var reached = walk.Solve(region);
        foreach (var (block, index, locations) in wanted)
        {
            if (locations.Count == 0 || reached[block.Index] is not { } entered)
                continue;
            var state = walk.Before(block, entered, index);
            foreach (var location in locations)
            {
                var value = state.Values[location];
                found[location] = found.TryGetValue(location, out var known) ? MemoryWalk.Value.Merge(known, value) : value;
            }
        }
        return found;
    }

    /// <summary>
    /// Returns every register and flag as an input, with where the value it holds before
    /// <paramref name="step"/> was set. A 16-bit accumulator holds both of its halves, so either
    /// half's sources are the accumulator's, and an 8-bit one holds only its low byte.
    /// </summary>
    private static IEnumerable<(int Order, string Name, InputCategory Category, SourceValue Value)> Holding(
        FileAnalysis file, Step step, SourceState state)
    {
        var wide = file.State?.Before(step.Statement, step.On)?.Processor.A == Semantics.Width.Sixteen;
        foreach (var register in RegisterEffects.Each(Registers.All))
        {
            yield return Register(register, register == Registers.A && wide
                ? state.Whole(register)
                : state.Of(SourceState.Track(register)));
        }
    }

    /// <summary>
    /// Returns the name of an input that two calls, or two copies of one line, both read under
    /// different names. That happens only for a width, where the routines a <c>.next</c> names need
    /// different ones, and the name then lists each, narrowest first, as <c>a8/a16</c>.
    /// </summary>
    private static string Joined(string known, string name) =>
        known == name ? known
            : string.Join("/", known.Split('/').Append(name).Distinct()
                .OrderBy(each => each.Length).ThenBy(each => each, StringComparer.Ordinal));

    /// <summary>Returns how many bits wide a known width is, which is 8 or 16.</summary>
    private static int Bits(Semantics.Width width) => width == Semantics.Width.Eight ? 8 : 16;

    /// <summary>Returns the registers the instruction at <paramref name="step"/> reads.</summary>
    private static Registers ReadBy(SourceWalk walk, Step step, InstructionStatementSyntax statement) =>
        RegisterEffects.Read(statement.MnemonicKind, walk.ModeOf(step));

    /// <summary>Returns an input for one register, with the order key and name it is reported under.</summary>
    private static (int Order, string Name, InputCategory Category, SourceValue Value) Register(Registers register, SourceValue value) =>
        ((int)SourceState.Track(register), RegisterEffects.Format(register),
            (register & Registers.Flags) != Registers.None ? InputCategory.Flag : InputCategory.Register, value);

    /// <summary>
    /// Maps the steps a walk names to spans in the caret's file. A step from a macro expansion maps to
    /// the outermost macro call in the file, as a diagnostic in a macro body is reported there.
    /// </summary>
    private sealed class Mapping
    {
        private readonly SyntaxTree tree;
        private readonly Func<Symbol, RoutineRegisters> of;
        private readonly Dictionary<StepKey, (Step Step, BasicBlock Block)> steps = [];

        public Mapping(SyntaxTree tree, FlowRegion region, Func<Symbol, RoutineRegisters> of)
        {
            this.tree = tree;
            this.of = of;
            foreach (var block in region.Blocks)
            {
                foreach (var step in block.Steps)
                    steps.TryAdd(step.Key, (step, block));
            }
            Routine = region.Routine.Tree == tree ? region.Routine.NameSpan
                : region.Blocks.SelectMany(block => block.Steps).Select(step => Span(step)?.Span).FirstOrDefault(span => span is not null)
                    ?? default;
        }

        /// <summary>Gets the span of the routine's name, on the line that opens it.</summary>
        public TextSpan Routine { get; }

        /// <summary>Returns the input as it is reported, with each source and through line mapped to the file.</summary>
        public SourcedInput Input(string name, InputCategory category, SourceValue value)
        {
            var sources = new List<InputSource>();
            foreach (var origin in value.Origins.Keys)
            {
                if (Source(origin) is { } source && !sources.Any(known => known.Kind == source.Kind && known.Line == source.Line))
                    sources.Add(source);
            }
            var through = value.Through
                .Select(key => steps.TryGetValue(key, out var at) ? Span(at.Step)?.Span : null)
                .OfType<TextSpan>()
                .Distinct()
                .OrderBy(span => span.Start)
                .ToList();
            return new SourcedInput(
                name, null, category, [.. sources.OrderBy(source => source.Line.Start).ThenBy(source => source.Kind)], through, []);
        }

        /// <summary>
        /// Returns a location in memory as it is reported. Every source of it is a best guess, and
        /// where something might also have changed the value since, each source says what.
        /// </summary>
        public SourcedInput Memory(Location location, MemoryWalk.Value value)
        {
            var possibly = value.Doubts
                .Select(key => steps.TryGetValue(key, out var at) ? Span(at.Step)?.Span : null)
                .OfType<TextSpan>()
                .Distinct()
                .OrderBy(span => span.Start)
                .ToList();
            var reason = possibly.Count == 0 ? null : "or possibly " + string.Join(", ", possibly.Select(span =>
                $"`{tree.Text[span.Start..span.End].Trim()}` on line {tree.GetLineIndex(span.Start) + 1}"));
            var sources = new List<InputSource>();
            foreach (var origin in value.Origins)
            {
                if (Source(origin) is { } source && !sources.Any(known => known.Kind == source.Kind && known.Line == source.Line))
                    sources.Add(source with { Confidence = SourceConfidence.BestEffort, Reason = reason });
            }
            return new SourcedInput(
                location.Name, location.Group, InputCategory.Memory,
                [.. sources.OrderBy(source => source.Line.Start).ThenBy(source => source.Kind)], [], possibly);
        }

        /// <summary>Returns the source an origin stands for, or null where its step is not in this file.</summary>
        private InputSource? Source(Origin origin)
        {
            if (origin.Kind == SourceKind.Entry)
                return new InputSource(SourceKind.Entry, Routine, SourceConfidence.Proven, null, null);
            if (origin.At is not { } key || !steps.TryGetValue(key, out var at) || Span(at.Step) is not var (span, inMacro))
                return null;
            return origin.Kind switch
            {
                SourceKind.Unknown => new InputSource(SourceKind.Unknown, span, SourceConfidence.Proven, span, Why(at.Step, at.Block)),
                SourceKind.Instruction when inMacro => new InputSource(SourceKind.Macro, span, SourceConfidence.Proven, null, null),
                _ => new InputSource(origin.Kind, span, SourceConfidence.Proven, null, null),
            };
        }

        /// <summary>
        /// Returns the span a step is shown at in the caret's file, as <see cref="StepLines.Of"/> finds it.
        /// </summary>
        private (TextSpan Span, bool InMacro)? Span(Step step) => StepLines.Of(tree, step);

        /// <summary>Returns a short phrase saying why the analysis lost track of a value at <paramref name="step"/>.</summary>
        private string Why(Step step, BasicBlock block)
        {
            var text = step.Statement.GetText().Trim();
            if (step.Statement is StateDirectiveSyntax && block.Label is { } label)
                return $"`{label.DisplayName}` can be entered from outside the routine";
            if (step.Statement is not InstructionStatementSyntax statement)
                return $"nt65 lost track at `{text}`";

            var mnemonic = statement.MnemonicKind;
            if (mnemonic is MnemonicKind.Brk or MnemonicKind.Cop)
                return $"`{text}` runs a handler nt65 cannot follow";
            if (step == block.Steps[^1] && RegisterWalk.CallsAtEnd(block))
            {
                if (block.CallsUnknown || block.Calls.Count == 0)
                    return block.Next is null ? $"`{text}` has no `.next`" : $"the `.next` under `{text}` names no routine";
                if (block.Calls.FirstOrDefault(callee => !of(callee).Complete) is { } incomplete)
                {
                    return of(incomplete) == RoutineRegisters.Nothing
                        ? $"`{incomplete.DisplayName}` does not declare what it keeps"
                        : $"`{incomplete.DisplayName}` makes calls nt65 cannot follow";
                }
            }
            if (mnemonic == MnemonicKind.Rti)
                return $"`{text}` returns with something still pushed";
            if (Instructions.Facts(mnemonic).Pulls is not null)
                return $"`{text}` does not take back a push nt65 followed";
            return $"nt65 lost track at `{text}`";
        }
    }
}
