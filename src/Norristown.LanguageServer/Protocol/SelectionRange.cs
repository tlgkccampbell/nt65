namespace Norristown.LanguageServer.Protocol;

/// <summary>
/// Represents one step in widening a selection from the caret. The steps go from an operand to
/// the instruction that contains it, then to the block, the routine and the file.
/// </summary>
/// <param name="Range">The range this step selects.</param>
/// <param name="Parent">The next wider step, or null at the file.</param>
internal sealed record SelectionRange(Range Range, SelectionRange? Parent);