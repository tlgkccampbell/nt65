using Norristown.Semantics;

namespace Norristown.Layout;

/// <summary>
/// Represents what runs from a position a <c>.label</c> names inside an instruction. That is the
/// instructions its bytes decode as, until the decoded bytes reach the start of an instruction as
/// written, where control goes on, or decode as a return, where the path leaves the routine.
/// </summary>
/// <param name="Label">The name the <c>.label</c> gives the position.</param>
/// <param name="Instructions">
/// The instructions the bytes run as, in order, each of which runs on into the next. A return that
/// ends the path is not among them.
/// </param>
/// <param name="Landing">
/// The step of the instruction the decoded bytes reach the start of, or null where the path ends in
/// <see cref="Return"/> instead.
/// </param>
/// <param name="Return">
/// The <c>rts</c> or <c>rtl</c> the path ends in, which returns to the routine's caller as one
/// written there would, or null where the path reaches <see cref="Landing"/>.
/// </param>
internal sealed record HiddenPath(
    Symbol Label, IReadOnlyList<HiddenInstruction> Instructions, StepKey? Landing, HiddenInstruction? Return = null);
