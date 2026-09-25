using Norristown.Processor;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// Works out whether a routine entered at one of its labels pulls what its caller pushed. The
/// answer for each label that is an entry point of its own tells <see cref="CallerStack"/> which
/// routines depend on the depth of the stack they were entered with.
/// </summary>
internal static class StackPulls
{
    /// <summary>
    /// Returns whether <paramref name="region"/>'s routine, entered at the block at
    /// <paramref name="start"/> and followed with <paramref name="walk"/>, may pull more than it
    /// has pushed on the way, which takes what its caller pushed. A pull where what is on the
    /// stack is not known counts.
    /// </summary>
    public static bool Below(RegisterWalk walk, FlowRegion region, Func<Symbol, RoutineRegisters> of, int start)
    {
        var reached = walk.Solve(region, of, start);
        foreach (var block in region.Blocks)
        {
            if (reached[block.Index] is not { } state)
                continue;
            foreach (var step in block.Steps)
            {
                if (step.Statement is InstructionStatementSyntax statement
                    && Instructions.Facts(statement.MnemonicKind).Pulls is not null
                    && state.Stack is not { Depth: > 0 })
                {
                    return true;
                }
                state = walk.Step(step, state, null);
            }
        }
        return false;
    }
}
