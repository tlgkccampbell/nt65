using Norristown.Tests.Semantics;

namespace Norristown.Tests.Flow;

/// <summary>
/// Checks where each routine runs from: under an interrupt, in the rest of the program, or both.
/// </summary>
public sealed class RoutineContextsTests
{
    /// <summary>
    /// A routine only a handler calls runs under the interrupt, one only the program calls runs in
    /// main, and one both call runs in both. A handler runs under itself.
    /// </summary>
    [Fact]
    public void CallsCarryTheContextOfTheirCaller()
    {
        Assert.Equal(
            [
                "main Main",
                "util Main, Interrupt under nmi",
                "play Interrupt under nmi",
                "load Main",
                "nmi Interrupt under nmi",
            ],
            Render("6502", """
                .export .proc main {
                    jsr util
                    jsr load
                    rts
                }
                .proc util {
                    rts
                }
                .proc play {
                    jsr util
                    rts
                }
                .proc load {
                    rts
                }
                .export .proc nmi: interrupt {
                    jsr play
                    rti
                }
                """));
    }

    /// <summary>
    /// The walk follows control out of a routine by a tail jump, a branch out of the routine, a
    /// <c>.next</c> under a jump and a <c>.fallthrough</c>, not only by calls. None of the routines
    /// reached that way is where the rest of the program starts.
    /// </summary>
    [Fact]
    public void EveryWayOutOfARoutineIsFollowed()
    {
        Assert.Equal(
            [
                "main Main",
                "irq Interrupt under irq",
                "branched Interrupt under irq",
                "jumped Interrupt under irq",
                "fancy Interrupt under irq",
                "plain Interrupt under irq",
                "fell Interrupt under irq",
            ],
            Render("6502", """
                .data table: .addr
                .export .proc main {
                    rts
                }
                .export .proc irq: interrupt {
                    lda $d019
                    bne branched
                    jmp jumped
                }
                .proc branched {
                    jmp (table)
                    .next fancy, plain
                }
                .proc jumped {
                    rts
                }
                .proc fancy {
                    rts
                }
                .proc plain {
                    .fallthrough fell
                }
                .proc fell {
                    rts
                }
                """));
    }

    /// <summary>
    /// A handler's walk does not go into another handler it jumps to, because that handler is
    /// interrupted into rather than called.
    /// </summary>
    [Fact]
    public void AHandlerDoesNotReachAnotherHandler()
    {
        Assert.Equal(
            [
                "first Interrupt under first",
                "second Interrupt under second",
                "shared Interrupt under second",
            ],
            Render("6502", """
                .export .proc first: interrupt {
                    jmp second
                }
                .export .proc second: interrupt {
                    jsr shared
                    rti
                }
                .proc shared {
                    rts
                }
                """));
    }

    /// <summary>
    /// A transfer whose target nt65 cannot identify stops the walk, and is listed so that a reader
    /// knows the routines past it are not marked.
    /// </summary>
    [Fact]
    public void AWalkStopsWhereTheTargetIsNotKnown()
    {
        Assert.Equal(
            [
                "main Main",
                "irq Interrupt under irq stops at 9",
                "elsewhere Main",
            ],
            Render("6502", """
                .data vector: .addr
                .export .proc main {
                    rts
                }
                .export .proc irq: interrupt {
                    jsr dispatch
                    rti
                dispatch:
                    jmp (vector)
                    .next ?
                }
                .export .proc elsewhere {
                    rts
                }
                """));
    }

    /// <summary>
    /// Returns one line per routine of <c>main.nt65</c>, in the order it declares them, giving the
    /// routine's context, the handlers that reach it and the lines where the walk stops in it,
    /// counted from the start of the test's own text.
    /// </summary>
    private static List<string> Render(string cpu, string text)
    {
        var analysis = FlowFragment.Analyze(cpu, text);
        var contexts = analysis.Contexts();
        var tree = analysis.File(Analysis.Path).Tree;
        return [.. analysis.FlowFor(Analysis.Path)!.Regions
            .Select(region => analysis.Program.Current(region.Routine))
            .Distinct()
            .Select(routine =>
            {
                var handlers = contexts.HandlersOf(routine);
                var stops = contexts.Unfollowed(routine);
                return $"{routine.Name} {contexts.Of(routine)}"
                    + (handlers.Count > 0 ? $" under {string.Join(", ", handlers.Select(handler => handler.Name))}" : "")
                    + (stops.Count > 0 ? $" stops at {string.Join(", ", stops.Select(span => tree.GetLineIndex(span.Start) + 1 - FlowFragment.HeaderLines))}" : "");
            })];
    }
}
