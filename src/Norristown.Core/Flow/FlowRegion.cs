using Norristown.Semantics;

namespace Norristown.Flow;

/// <summary>
/// Represents one routine as blocks and the edges between them. A nested segment block is part
/// of it. Fall-through never enters a nested segment block, and the statement after the block
/// follows the statement before it, but a jump may go into the block and back out.
/// </summary>
public sealed class FlowRegion
{
    internal FlowRegion(
        Symbol routine, bool entered, IReadOnlyList<BasicBlock> blocks, RoutineCost cost,
        IReadOnlyList<ScopeCost> scopes, IReadOnlyList<(Syntax.TextSpan Opener, Syntax.TextSpan Whole)> inline)
    {
        Routine = routine;
        IsEntered = entered;
        Blocks = blocks;
        Cost = cost;
        Scopes = scopes;
        Inline = inline;
    }

    /// <summary>Gets the routine.</summary>
    public Symbol Routine { get; }

    /// <summary>Gets a value indicating whether its first block is where a call to the routine enters it.</summary>
    public bool IsEntered { get; }

    /// <summary>Gets the cost of one pass through the routine.</summary>
    public RoutineCost Cost { get; internal set; }

    /// <summary>
    /// Gets the cost of one pass through the routine with what it calls, worked out across the
    /// program.
    /// </summary>
    public RoutineCost Total { get; internal set; }

    /// <summary>
    /// Gets which registers the routine returns holding the values it was entered with, worked
    /// out across the program.
    /// </summary>
    public RoutineRegisters Registers { get; internal set; } = RoutineRegisters.Everything;

    /// <summary>Gets the cost of one pass through each inline <c>.scope</c> block of the routine.</summary>
    public IReadOnlyList<ScopeCost> Scopes { get; }

    /// <summary>
    /// Gets every inline <c>.scope</c> block of the file, as the span of the line that opens it
    /// and the span of the whole block. They belong to the file rather than this routine, and a
    /// scope in another routine simply contains none of this routine's statements.
    /// </summary>
    public IReadOnlyList<(Syntax.TextSpan Opener, Syntax.TextSpan Whole)> Inline { get; }

    /// <summary>
    /// Gets which registers each inline <c>.scope</c> block of the routine leaves holding the
    /// values they had when the block was entered, worked out across the program.
    /// </summary>
    public IReadOnlyList<ScopeRegisters> ScopeRegisters { get; internal set; } = [];

    /// <summary>
    /// Gets the routine's blocks. Those of the routine's own stream of bytes come first, then those
    /// of each nested segment block, each stream in the order its bytes are emitted.
    /// </summary>
    public IReadOnlyList<BasicBlock> Blocks { get; }
}
