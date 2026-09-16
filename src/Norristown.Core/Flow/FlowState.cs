using Norristown.Semantics;

namespace Norristown.Flow;

/// <summary>
/// Everything the processor-state analysis knows at one point in a routine: the widths, the
/// mode, the direct page and the data bank, and what the routine has pushed.
/// </summary>
/// <param name="Processor">The widths, the mode, the direct page and the data bank.</param>
/// <param name="Stack">What the routine has pushed, or null when that is not known.</param>
public sealed record FlowState(ProcessorState Processor, AnalysisStack? Stack)
{
    /// <summary>
    /// What two paths arriving at one place agree on. Where they disagree the part is
    /// unknown, and nothing is reported: an unknown value is an error only where it is used.
    /// </summary>
    public static FlowState Merge(FlowState? known, FlowState arriving)
    {
        if (known is null)
            return arriving;
        var a = known.Processor;
        var b = arriving.Processor;
        return new FlowState(
            new ProcessorState(
                a.A == b.A ? a.A : Width.Unknown,
                a.Index == b.Index ? a.Index : Width.Unknown,
                a.E == b.E ? a.E : ProcessorMode.Unknown,
                StateValue.Merge(a.D, b.D),
                StateValue.Merge(a.B, b.B)),
            AnalysisStack.Merge(known.Stack, arriving.Stack));
    }
}
