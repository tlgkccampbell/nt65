using Norristown.Layout;
using Norristown.Processor;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// Finds the exported routines whose bytes depend on a part of their entry that is inferred from
/// their callers in the program rather than declared. A caller outside nt65 is not checked against
/// an inferred part, so each such routine gets a hint whose fix declares the parts it depends on.
/// <para>
/// A part counts only where an emitted byte depends on it. A width counts where it sizes an
/// immediate, which the width directive before the immediate follows. The mode counts where it
/// decides such a width, and the direct page where a <c>d:</c> operand is written through it.
/// Each part is tested by entering the routine again with that part as an unseen caller could
/// leave it, which is <c>*</c>, and comparing the states the two entries lead to.
/// </para>
/// </summary>
internal static class InferredExports
{
    // The parts of the entry that decide the routine's bytes. The data bank decides only which
    // memory an operand reaches, so it is not one of them.
    private static readonly StateParts[] parts = [StateParts.A, StateParts.Index, StateParts.Mode, StateParts.DirectPage];

    /// <summary>
    /// Returns a hint for each exported routine of <paramref name="flow"/>'s file whose bytes
    /// depend on an inferred part of its entry.
    /// </summary>
    /// <param name="layout">The file's layout, which gives each instruction's addressing mode.</param>
    /// <param name="flow">The file's control flow.</param>
    /// <param name="signatures">The signatures the file was analyzed with.</param>
    /// <param name="analysis">The file's processor-state analysis, which gives the state at each statement.</param>
    /// <param name="enteredWith">
    /// Returns the state at each statement of a region where its routine is entered with the state given.
    /// </param>
    /// <returns>The hints, in the order the routines come in the file.</returns>
    public static IReadOnlyList<Diagnostic> Of(
        CodeLayout layout, ControlFlow flow, InferredSignatures signatures, StateAnalysis analysis,
        Func<FlowRegion, ProcessorState, Dictionary<StepKey, ProcessorState>> enteredWith)
    {
        List<Diagnostic>? found = null;
        foreach (var region in flow.Regions)
        {
            if (region is not { IsEntered: true, Blocks.Count: > 0, Routine: { IsExported: true } routine }
                || routine.Signature is not { IsInterrupt: false } declared
                || signatures.Of(routine) is not { } inferred)
            {
                continue;
            }
            var relied = parts
                .Where(part => (declared.Declared & part) == 0 && Unseen(inferred.Entry, part) is { } unseen
                    && DependsOn(layout, region, analysis, enteredWith(region, unseen)))
                .ToList();
            if (relied.Count == 0)
                continue;

            var entry = relied.Select(part => Item(inferred.Entry, part)!).ToList();
            var exit = inferred.NeverReturns ? [] : relied.Select(part => Item(inferred.Exit, part)).OfType<string>().ToList();
            var named = entry.Select(item => $"`{item}`").ToList();
            var listed = named.Count == 1 ? named[0] : $"{string.Join(", ", named[..^1])} and {named[^1]}";
            (found ??= []).Add(new Diagnostic(routine.DeclarationSpan, Catalog.ExportStateInferred.Message(routine.DisplayName, listed))
            {
                Fix = new DiagnosticFix(
                    FixKind.Inferred,
                    exit.Count == 0 ? string.Join(", ", entry) : $"{string.Join(", ", entry)} -> {string.Join(", ", exit)}",
                    routine.DeclarationSpan),
            });
        }
        return found ?? [];
    }

    /// <summary>
    /// Returns <paramref name="entry"/> with <paramref name="part"/> as a caller nothing checks
    /// may leave it, which is <c>*</c>, or null where the part has no value to depend on.
    /// </summary>
    private static ProcessorState? Unseen(ProcessorState entry, StateParts part) => part switch
    {
        StateParts.A when StateChecks.IsKnown(entry.A) => entry with { A = Width.Unchanged },
        StateParts.Index when StateChecks.IsKnown(entry.Index) => entry with { Index = Width.Unchanged },
        StateParts.Mode when StateChecks.IsKnown(entry.E) => entry with { E = ProcessorMode.Unchanged },
        StateParts.DirectPage when entry.D.Kind == StateValueKind.Known => entry with { D = StateValue.Unchanged },
        _ => null,
    };

    /// <summary>
    /// Returns a value indicating whether some byte of the region comes out differently where the
    /// routine is entered as <paramref name="unseen"/> gives. That is an immediate whose width
    /// is known and differs there, or a <c>d:</c> operand whose direct page is known and differs.
    /// </summary>
    private static bool DependsOn(
        CodeLayout layout, FlowRegion region, StateAnalysis analysis, Dictionary<StepKey, ProcessorState> unseen)
    {
        foreach (var step in region.Blocks.SelectMany(block => block.Steps))
        {
            if (step.Statement is not InstructionStatementSyntax instruction
                || analysis.Before(instruction, step.On)?.Processor is not { } known
                || !unseen.TryGetValue(step.Key, out var other))
            {
                continue;
            }
            if (layout.Of(instruction, step.On)?.Mode == AddressingMode.Immediate
                && Instructions.SizedBy(instruction.MnemonicKind) is { } register
                && StateChecks.IsKnown(known.Of(register)) && other.Of(register) != known.Of(register))
            {
                return true;
            }
            if (instruction.Operand is { } operand && CodeLayout.ThroughDirectPage(operand)
                && known.D.Kind == StateValueKind.Known && other.D != known.D)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Returns <paramref name="part"/> of <paramref name="state"/> as a signature item, or null where
    /// it is as the routine was entered with, which needs no item.
    /// </summary>
    private static string? Item(ProcessorState state, StateParts part) => part switch
    {
        StateParts.A => state.A == Width.Unchanged ? null : ProcessorState.Format(StateRegister.A, state.A),
        StateParts.Index => state.Index == Width.Unchanged ? null : ProcessorState.Format(StateRegister.Index, state.Index),
        StateParts.Mode => state.E == ProcessorMode.Unchanged ? null : ProcessorState.Format(state.E),
        _ => state.D.Kind == StateValueKind.Unchanged ? null : state.D.Format(StateRegister.DirectPage),
    };
}
