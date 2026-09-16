using Norristown.Semantics;

namespace Norristown.Flow;

/// <summary>
/// One stream of bytes inside one routine, as blocks and the edges between them. A nested
/// segment block is a region of its own: fall-through never enters it, and the statement
/// after it follows the statement before it.
/// </summary>
public sealed class FlowRegion
{
    internal FlowRegion(Symbol routine, int stream, bool entered, IReadOnlyList<BasicBlock> blocks)
    {
        Routine = routine;
        Stream = stream;
        IsEntered = entered;
        Blocks = blocks;
    }

    /// <summary>The routine the region is part of.</summary>
    public Symbol Routine { get; }

    /// <summary>Which stream of bytes it is.</summary>
    public int Stream { get; }

    /// <summary>Whether this is the region a call to the routine enters, rather than a detour in it.</summary>
    public bool IsEntered { get; }

    /// <summary>Its blocks, in the order their bytes are written.</summary>
    public IReadOnlyList<BasicBlock> Blocks { get; }
}
