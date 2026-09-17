namespace Norristown.Flow;

/// <summary>One loop of a routine: where a path comes back to, where it comes back from, and what it holds.</summary>
/// <param name="Header">The block every turn starts at, which the back edge goes to.</param>
/// <param name="Latch">The block the back edge comes from, which is where a turn ends.</param>
/// <param name="Inside">Which of the routine's blocks the loop holds.</param>
internal readonly record struct Loop(int Header, int Latch, bool[] Inside);
