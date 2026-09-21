namespace Norristown.LanguageServer.Protocol;

/// <summary>
/// One step of what a caret grows to take in when the selection is widened: an operand, then
/// the instruction written around it, then the block, then the routine, then the file.
/// </summary>
/// <param name="Range">What this step selects.</param>
/// <param name="Parent">The step that takes it in, or null at the file.</param>
internal sealed record SelectionRange(Range Range, SelectionRange? Parent);