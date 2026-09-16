namespace Norristown.Tests.Oracle;

/// <summary>What ld65 made of a set of object files.</summary>
/// <param name="Succeeded">Whether ca65 and ld65 both finished with nothing to say.</param>
/// <param name="Messages">What they said, when they said anything.</param>
/// <param name="Binary">The linked image, empty when the link failed.</param>
internal sealed record LinkResult(bool Succeeded, string Messages, byte[] Binary);
