using Norristown.Layout;
using Norristown.Processor;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// Represents where each value that the instruction at the caret writes is read, for an editor to
/// show. It is the dual of <see cref="InputSources"/>. Each value the instruction writes is an
/// <em>output</em>, and a <em>reader</em> is a place that reads it before anything writes it again,
/// on a path from the caret.
/// <para>
/// A call whose routine reads the value is a reader. So is each place the value leaves the
/// routine, since what the routine hands control back to may read it. That is why the answer never
/// says a value is dead.
/// </para>
/// <para>
/// The answer is only for showing. Nothing warns or errors because of it, which is what lets the
/// readers of a value in memory be a best guess.
/// </para>
/// </summary>
/// <param name="Routine">The span of the routine's name, on the line that opens the routine.</param>
/// <param name="Outputs">
/// Each output that something reads, in a fixed order: registers, then flags, then widths, then
/// memory. An output nothing reads is left out.
/// </param>
public sealed record OutputReaders(TextSpan Routine, IReadOnlyList<ReadOutput> Outputs)
{
    /// <summary>
    /// Returns where each output of the instruction on the line at <paramref name="position"/> in
    /// <paramref name="model"/>'s file is read. It returns null where the line holds no
    /// instruction, where the line is inside a macro's definition, whose expansions would each give
    /// a different answer, and where the analysis reached no answer.
    /// </summary>
    public static OutputReaders? At(ProgramAnalysis analysis, SemanticModel model, int position)
    {
        var tree = model.Tree;
        if (position >= tree.Text.Length
            || tree.GetLine(tree.GetLineIndex(position)).Statement is not InstructionStatementSyntax statement
            || statement.Ancestors().OfType<BlockSyntax>().Any(block => block.Opener.Statement is MacroDeclarationSyntax)
            || analysis.FileFor(tree.Path) is not { } file
            || file.Flow.KeepsOf is not { } keepsOf
            || file.Flow.ReadsOf is not { } readsOf)
        {
            return null;
        }

        foreach (var region in file.Flow.Regions)
        {
            var caret = (
                from block in region.Blocks
                from step in block.Steps
                where !step.Closes && step.Statement.Tree == tree && step.Statement.Position == statement.Position
                select step).ToList();
            if (caret.Count == 0)
                continue;
            var keys = caret.Select(step => step.Key).ToHashSet();

            var walk = new SourceWalk(model, file.Layout, file.Flow, file.State, keepsOf);
            var reached = walk.Solve(region);
            if (!region.Blocks.Any(block => reached[block.Index] is not null && block.Steps.Any(step => keys.Contains(step.Key))))
                return null;

            var found = new SortedDictionary<(int Order, string Name), (string? Group, InputCategory Category, List<OutputReader> Readers)>();
            foreach (var block in region.Blocks)
            {
                if (reached[block.Index] is not { } state)
                    continue;
                for (var index = 0; index < block.Steps.Count; index++)
                {
                    foreach (var (order, name, category, value) in InputSources.InputsOf(walk, block, index, state, readsOf))
                    {
                        if (From(value, keys) && Reader(tree, block, index) is { } reader)
                            Add(found, (order, name), null, category, reader);
                    }
                    state = walk.After(block, index, state);
                }

                // What the routine hands control back to may read what the block leaves.
                if (IsExit(block) && Exit(tree, block) is { } exit)
                {
                    foreach (var register in RegisterEffects.Each(Registers.All))
                    {
                        // Either half of the accumulator may leave with the caret's value.
                        var track = SourceState.Track(register);
                        if (From(register == Registers.A ? state.Whole(register) : state.Of(track), keys))
                            Add(found, ((int)track, RegisterEffects.Format(register)), null, Category(register), exit);
                    }
                }
            }
            foreach (var (location, readers) in Memory(analysis, file, region, caret, keys, tree))
            {
                foreach (var reader in readers)
                    Add(found, (int.MaxValue, location.Name), location.Group, InputCategory.Memory, reader);
            }
            var routine = region.Routine.Tree == tree ? region.Routine.NameSpan
                : caret.Select(step => StepLines.Of(tree, step)?.Span).FirstOrDefault(span => span is not null) ?? default;
            return new OutputReaders(routine, [
                .. found.Select(output => new ReadOutput(
                    output.Key.Name,
                    output.Value.Group,
                    output.Value.Category,
                    [.. output.Value.Readers.OrderBy(reader => reader.Line.Start).ThenBy(reader => reader.Kind)])),
            ]);
        }
        return null;
    }

    /// <summary>
    /// Returns where each location in memory that the instruction at the caret stores to directly is
    /// read. A reader is a later instruction that reads one of the location's bytes, or a call to a
    /// routine that <see cref="MemoryInference"/> finds reads it, wherever the value reaching it may
    /// be the caret's. Every place the routine hands control back from is a reader too, because
    /// memory outlives the routine. All of these are best guesses.
    /// </summary>
    private static IEnumerable<(Location Location, IReadOnlyList<OutputReader> Readers)> Memory(
        ProgramAnalysis analysis, FileAnalysis file, FlowRegion region, IReadOnlyList<Step> caret,
        IReadOnlySet<StepKey> keys, SyntaxTree tree)
    {
        var written = caret
            .Select(step => MemoryAccess.Of(file, step))
            .Where(access => access is { Stores: true, Direct: not null })
            .SelectMany(access => access!.Value.DirectBytes)
            .Distinct()
            .ToList();
        if (written.Count == 0)
            yield break;

        var inference = new MemoryInference(analysis);
        var walk = new MemoryWalk(file, inference, written);
        var reached = walk.Solve(region);
        var found = written.ToDictionary(location => location, _ => new List<OutputReader>());
        foreach (var block in region.Blocks)
        {
            if (reached[block.Index] is not { } entered)
                continue;
            for (var index = 0; index < block.Steps.Count; index++)
            {
                IEnumerable<Location> read = index == block.Steps.Count - 1 && RegisterWalk.CallsAtEnd(block)
                    ? block.Calls.SelectMany(inference.ReadsOf)
                    : MemoryAccess.Of(file, block.Steps[index]) is { Reads: true } access ? [.. access.DirectBytes, .. access.Pointer] : [];
                var wanted = read.Where(found.ContainsKey).Distinct().ToList();
                if (wanted.Count == 0 || Reader(tree, block, index) is not { } reader)
                    continue;
                var state = walk.Before(block, entered, index);
                foreach (var location in wanted.Where(location => From(state.Values[location], keys)))
                    found[location].Add(reader with { Confidence = SourceConfidence.BestEffort });
            }
            if (IsExit(block) && Exit(tree, block) is { } exit)
            {
                var left = walk.Before(block, entered, block.Steps.Count);
                foreach (var location in written.Where(location => From(left.Values[location], keys)))
                    found[location].Add(exit with { Confidence = SourceConfidence.BestEffort });
            }
        }
        foreach (var (location, readers) in found.OrderBy(pair => pair.Key.Name, StringComparer.Ordinal))
        {
            if (readers.Count > 0)
                yield return (location, readers);
        }
    }

    /// <summary>Checks whether a register's value may be one a caret step set.</summary>
    private static bool From(SourceValue value, IReadOnlySet<StepKey> keys) =>
        value.Origins.Keys.Any(origin => Set(origin, keys));

    /// <summary>Checks whether a location's value may be one a caret step set.</summary>
    private static bool From(MemoryWalk.Value value, IReadOnlySet<StepKey> keys) =>
        value.Origins.Any(origin => Set(origin, keys));

    /// <summary>
    /// Checks whether <paramref name="origin"/> is a caret step setting a value, which an
    /// instruction does and a call does for what its routine does not keep.
    /// </summary>
    private static bool Set(Origin origin, IReadOnlySet<StepKey> keys) =>
        origin.Kind is SourceKind.Instruction or SourceKind.Call && origin.At is { } at && keys.Contains(at);

    /// <summary>
    /// Checks whether control leaves the routine after <paramref name="block"/>, by a return, a
    /// tail call, a jump nt65 cannot follow, or a run or a branch into another routine. A call that
    /// never returns and an instruction that stops the processor leave nothing to read the value.
    /// </summary>
    private static bool IsExit(BasicBlock block) =>
        block.Steps.Count > 0
        && (block.End is BlockEnd.Return or BlockEnd.TailCall or BlockEnd.Elsewhere or BlockEnd.Declared or BlockEnd.Fallthrough
            || block.RunsInto is not null || block.BranchesOut);

    /// <summary>Returns the reader that the step at <paramref name="index"/> in <paramref name="block"/> stands for, or null outside the file.</summary>
    private static OutputReader? Reader(SyntaxTree tree, BasicBlock block, int index)
    {
        if (StepLines.Of(tree, block.Steps[index]) is not var (span, inMacro))
            return null;
        var kind = index == block.Steps.Count - 1 && RegisterWalk.CallsAtEnd(block) ? ReaderKind.Call
            : inMacro ? ReaderKind.Macro
            : ReaderKind.Instruction;
        return new OutputReader(span, kind, SourceConfidence.Proven);
    }

    /// <summary>Returns the reader that stands for control leaving the routine after <paramref name="block"/>.</summary>
    private static OutputReader? Exit(SyntaxTree tree, BasicBlock block) =>
        StepLines.Of(tree, block.Steps[^1]) is var (span, _) ? new OutputReader(span, ReaderKind.Exit, SourceConfidence.Proven) : null;

    /// <summary>Returns the category a register or a flag is reported under.</summary>
    private static InputCategory Category(Registers register) =>
        (register & Registers.Flags) != Registers.None ? InputCategory.Flag : InputCategory.Register;

    /// <summary>Adds a reader to an output, unless the output already has that kind of reader on that line.</summary>
    private static void Add(
        SortedDictionary<(int Order, string Name), (string? Group, InputCategory Category, List<OutputReader> Readers)> found,
        (int Order, string Name) key,
        string? group,
        InputCategory category,
        OutputReader reader)
    {
        if (!found.TryGetValue(key, out var output))
            found[key] = output = (group, category, []);
        if (!output.Readers.Any(known => known.Line == reader.Line && known.Kind == reader.Kind))
            output.Readers.Add(reader);
    }
}
