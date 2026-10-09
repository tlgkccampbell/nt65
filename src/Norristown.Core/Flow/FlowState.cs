using Norristown.Processor;
using Norristown.Semantics;

namespace Norristown.Flow;

/// <summary>
/// Represents everything the processor-state analysis knows at one point in a routine. That is
/// the register widths, the mode, the direct page, the data bank, and what the routine has
/// pushed.
/// </summary>
/// <param name="Processor">The widths, the mode, the direct page and the data bank.</param>
/// <param name="Stack">What the routine has pushed, or null when that is not known.</param>
public sealed record FlowState(ProcessorState Processor, AnalysisStack? Stack)
{
    /// <summary>
    /// Gets the state past a call to a routine none of whose returns has been seen yet, while the
    /// program's signatures are being inferred. No path goes on from it until one is.
    /// </summary>
    public static FlowState Dead { get; } = new(ProcessorState.Unknown, null) { IsDead = true };

    /// <summary>Gets why A's width is unknown, when it is unknown and the analysis can tell why.</summary>
    public Cause? WhyA { get; init; }

    /// <summary>Gets why the index width is unknown, when it is unknown and the analysis can tell why.</summary>
    public Cause? WhyIndex { get; init; }

    /// <summary>Gets why the stack is unknown, when it is unknown and the analysis can tell why.</summary>
    public Cause? WhyStack { get; init; }

    /// <summary>Gets why D is unknown, when it is unknown and the analysis can tell why.</summary>
    public Cause? WhyD { get; init; }

    /// <summary>
    /// Gets the registers among A, X and Y that hold the stack pointer here, as X does after
    /// <c>tsx</c>. A store indexed by one of them writes into the bytes on the stack, as
    /// <see cref="StackWrites"/> tells.
    /// </summary>
    public Registers Pointing { get; init; }

    /// <summary>Gets a value indicating whether this is <see cref="Dead"/>, which no path goes on from.</summary>
    public bool IsDead { get; private init; }

    /// <summary>
    /// Returns what two paths arriving at one place agree on. Where they disagree, that part is
    /// unknown and nothing is reported, because an unknown value is an error only where it is
    /// used.
    /// </summary>
    public static FlowState Merge(FlowState? known, FlowState arriving)
    {
        if (known is null || known.IsDead)
            return arriving;
        if (arriving.IsDead)
            return known;
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
            WhyA = Why(a.A, b.A, known.WhyA, arriving.WhyA, StateRegister.A),
            WhyIndex = Why(a.Index, b.Index, known.WhyIndex, arriving.WhyIndex, StateRegister.Index),
            WhyD = merged.Processor.D.Kind == StateValueKind.Unknown ? known.WhyD ?? arriving.WhyD : null,
            WhyStack = stack is not null ? null
                : known.Stack is null || arriving.Stack is null ? known.WhyStack ?? arriving.WhyStack
                : Cause.StacksDiffer(known.Stack.Depth != arriving.Stack.Depth),
            Pointing = known.Pointing & arriving.Pointing,
        };
    }

    /// <summary>
    /// Returns why a merged width is unknown. The cause is the one either side had, or else the
    /// two paths disagreeing.
    /// </summary>
    private static Cause? Why(Width a, Width b, Cause? known, Cause? arriving, StateRegister register)
    {
        if (a == b)
            return a is Width.Eight or Width.Sixteen ? null : known ?? arriving;
        if (a is Width.Eight or Width.Sixteen && b is Width.Eight or Width.Sixteen)
        {
            return new Cause(
                $"the paths that reach here leave {register.Name} 8-bit on one and 16-bit on another",
                "an `.ensure` sets it whichever path was taken");
        }
        return known ?? arriving;
    }
}
