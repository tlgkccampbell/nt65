namespace Norristown.Tests.Oracle;

/// <summary>What came of assembling a set of files with ca65 and linking them with ld65.</summary>
/// <param name="Succeeded">Whether ca65 and ld65 both exited cleanly and printed nothing.</param>
/// <param name="Messages">Whatever they printed, if anything.</param>
/// <param name="Binary">The linked image, empty when the link failed.</param>
internal sealed record LinkResult(bool Succeeded, string Messages, byte[] Binary)
{
    /// <summary>What <c>--dbgfile</c> wrote, when the link was asked for one.</summary>
    public string DebugFile { get; init; } = "";
}
