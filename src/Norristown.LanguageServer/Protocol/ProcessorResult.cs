namespace Norristown.LanguageServer.Protocol;

/// <summary>
/// Represents the answer to an <c>nt65/processor</c> request. It holds the rows that the
/// instruction hover shows below its rule, as data, for the line the caret is on.
/// </summary>
/// <param name="Routine">The name of the routine that holds the caret.</param>
/// <param name="Line">
/// The line the rows are for. That is the caret's line, or where it holds no statement that runs,
/// the next line in the routine that does.
/// </param>
/// <param name="Rows">The rows, in the order they are shown.</param>
/// <param name="Callers">Each call in the program to the routine, which the reader may choose to see it entered from.</param>
/// <param name="Caller">
/// The call the stack rows go on through, or null where the request named none or one that no
/// longer calls the routine.
/// </param>
internal sealed record ProcessorResult(
    string Routine, int Line, IReadOnlyList<ProcessorRow> Rows, IReadOnlyList<ProcessorCaller> Callers, Location? Caller);
