namespace Norristown.Emit;

/// <summary>
/// Identifies what an <see cref="EmittedLine"/> is, as far as the passes in
/// <see cref="LinePasses"/> need to know. The emitter records it where it writes the line, so
/// the passes never have to recognize a line by its spelling.
/// </summary>
internal enum EmittedLineKind
{
    /// <summary>A line no pass treats specially.</summary>
    Other,

    /// <summary>A <c>.segment</c> directive.</summary>
    Segment,

    /// <summary>
    /// A line that declares a name, such as a label, a constant, or a named data line once its
    /// name has been lined up in front of it.
    /// </summary>
    Declaration,

    /// <summary>
    /// A line of data that assembles to exactly one byte, given by one value. The value is in
    /// <see cref="EmittedLine.Value"/>.
    /// </summary>
    Byte,
}
