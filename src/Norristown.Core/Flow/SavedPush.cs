using Norristown.Processor;
using Norristown.Semantics;

namespace Norristown.Flow;

/// <summary>One push a routine made, as far as saving and restoring a register goes.</summary>
/// <param name="Value">What was pushed.</param>
/// <param name="Size">How much of the stack it took.</param>
/// <param name="Width">
/// How wide the register was where <paramref name="Size"/> is a register's: a pull gets the
/// value back only where it is as wide again. Every CPU but the 65816 has one width, and a
/// routine that changes neither keeps <see cref="Width.Unchanged"/> at both ends.
/// </param>
public readonly record struct SavedPush(RegisterValue Value, PushSize Size, Width Width);
