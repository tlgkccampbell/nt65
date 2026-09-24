namespace Norristown.Flow;

/// <summary>
/// Represents one loop of a routine by the block a path comes back to, the block it comes back
/// from, and the blocks the loop contains.
/// </summary>
/// <param name="Header">The block every iteration starts at, which the back edge goes to.</param>
/// <param name="Latch">The block the back edge comes from, which is where an iteration ends.</param>
/// <param name="Inside">Which of the routine's blocks the loop contains.</param>
internal readonly record struct Loop(int Header, int Latch, bool[] Inside);
