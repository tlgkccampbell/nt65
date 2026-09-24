namespace Norristown.Tests.Oracle;

/// <summary>Represents the result of assembling a set of files with ca65 and linking them with ld65.</summary>
/// <param name="Succeeded">Whether ca65 and ld65 both exited cleanly and printed nothing.</param>
/// <param name="Messages">The messages ca65 and ld65 printed, if any.</param>
/// <param name="Binary">The linked image, empty when the link failed.</param>
internal sealed record LinkResult(bool Succeeded, string Messages, byte[] Binary)
{
    /// <summary>
    /// Gets the debug file that <c>--dbgfile</c> wrote when the link was asked for one, or an
    /// empty string otherwise.
    /// </summary>
    public string DebugFile { get; init; } = "";
}
