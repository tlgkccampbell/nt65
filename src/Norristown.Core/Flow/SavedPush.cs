using Norristown.Processor;
using Norristown.Semantics;

namespace Norristown.Flow;

/// <summary>Represents one push a routine made, as it bears on saving and restoring a register.</summary>
/// <param name="Value">What was pushed.</param>
/// <param name="Size">How much of the stack it took.</param>
/// <param name="Width">
/// How wide the register was, where <paramref name="Size"/> is a register's size. A pull
/// restores the value only when the register is that wide again. Every CPU but the 65816 has
/// only one width, and a routine that changes neither A's nor the index registers' width has
/// <see cref="Width.Unchanged"/> at both the push and the pull.
/// </param>
public readonly record struct SavedPush(RegisterValue Value, PushSize Size, Width Width);
