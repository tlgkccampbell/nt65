namespace Norristown.LanguageServer.Protocol;

/// <summary>
/// Represents the answer to an <c>nt65/sources</c> request, which says where each value that the
/// instruction at the caret reads was set. Every range in it is in the caret's document.
/// </summary>
/// <param name="Routine">The line that opens the routine holding the caret, where entry chips go.</param>
/// <param name="Inputs">Each input of the instruction, registers first, then flags, then widths.</param>
internal sealed record SourcesResult(Range Routine, IReadOnlyList<SourcesInput> Inputs);
