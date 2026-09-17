using Norristown.Semantics;

namespace Norristown.Flow;

/// <summary>
/// One routine, as blocks and the edges between them. A nested segment block is part of it:
/// fall-through never enters one, and the statement after it follows the statement before
/// it, but a jump may go into it and back out.
/// </summary>
public sealed class FlowRegion
{
    internal FlowRegion(
        Symbol routine, bool entered, IReadOnlyList<BasicBlock> blocks, RoutineCost cost, IReadOnlyList<ScopeCost> scopes)
    {
        Routine = routine;
        IsEntered = entered;
        Blocks = blocks;
        Cost = cost;
        Scopes = scopes;
    }

    /// <summary>The routine.</summary>
    public Symbol Routine { get; }

    /// <summary>Whether its first block is where a call to the routine enters it.</summary>
    public bool IsEntered { get; }

    /// <summary>What one pass through it costs.</summary>
    public RoutineCost Cost { get; internal set; }

    /// <summary>What one pass through it costs with what it calls, worked out across the program.</summary>
    public RoutineCost Total { get; internal set; }

    /// <summary>What one pass through each inline <c>.scope</c> block of it costs.</summary>
    public IReadOnlyList<ScopeCost> Scopes { get; }

    /// <summary>
    /// Its blocks: those of the routine's own stream of bytes first, then those of each nested
    /// segment block, each stream in the order its bytes are written.
    /// </summary>
    public IReadOnlyList<BasicBlock> Blocks { get; }
}
