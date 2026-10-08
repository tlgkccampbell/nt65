using Norristown.Semantics;

namespace Norristown.Layout;

/// <summary>
/// Represents what runs from a position a <c>.label</c> names inside an instruction: the
/// instructions its bytes decode as, until the decoded bytes reach the start of an instruction as
/// written, where control goes on.
/// </summary>
/// <param name="Label">The name the <c>.label</c> gives the position.</param>
/// <param name="Instructions">The instructions the bytes run as, in order.</param>
/// <param name="Landing">The step of the instruction the decoded bytes reach the start of.</param>
internal sealed record HiddenPath(Symbol Label, IReadOnlyList<HiddenInstruction> Instructions, StepKey Landing);
