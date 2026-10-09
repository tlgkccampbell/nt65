namespace Norristown.Layout;

/// <summary>
/// Represents what a conditional branch costs on each way out of it. A path that takes the branch
/// pays <see cref="Taken"/>, and one that falls through pays <see cref="NotTaken"/>. The
/// instruction's own count is the interval both lie in, which no single path pays.
/// </summary>
/// <param name="Taken">The cycles paid where the condition holds and control goes to the target.</param>
/// <param name="NotTaken">The cycles paid where the condition fails and control runs on.</param>
public readonly record struct BranchCycles(CycleCount Taken, CycleCount NotTaken);
