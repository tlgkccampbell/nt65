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
    /// <summary>Why A's width is unknown, when it is unknown and the analysis can tell why.</summary>
    public Cause? WhyA { get; init; }

    /// <summary>Why the index width is unknown, when it is unknown and the analysis can tell why.</summary>
    public Cause? WhyIndex { get; init; }

    /// <summary>Why the stack is unknown, when it is unknown and the analysis can tell why.</summary>
    public Cause? WhyStack { get; init; }

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
        var stack = AnalysisStack.Merge(known.Stack, arriving.Stack);
        var merged = new FlowState(
            new ProcessorState(
                a.A == b.A ? a.A : Width.Unknown,
                a.Index == b.Index ? a.Index : Width.Unknown,
                a.E == b.E ? a.E : ProcessorMode.Unknown,
                StateValue.Merge(a.D, b.D),
                StateValue.Merge(a.B, b.B)),
            stack);
        return merged with
        {
            WhyA = Why(a.A, b.A, known.WhyA, arriving.WhyA, "A"),
            WhyIndex = Why(a.Index, b.Index, known.WhyIndex, arriving.WhyIndex, "X and Y"),
            WhyStack = stack is null ? known.WhyStack ?? arriving.WhyStack : null,
        };
    }

    /// <summary>Why a merged width is unknown: the cause either side had, or the paths disagreeing.</summary>
    private static Cause? Why(Width a, Width b, Cause? known, Cause? arriving, string register)
    {
        if (a == b)
            return a is Width.Eight or Width.Sixteen ? null : known ?? arriving;
        if (a is Width.Eight or Width.Sixteen && b is Width.Eight or Width.Sixteen)
        {
            return new Cause(
                $"the paths that reach here leave {register} 8-bit on one and 16-bit on another",
                "an `.ensure` sets it whichever path was taken");
        }
        return known ?? arriving;
    }
}
