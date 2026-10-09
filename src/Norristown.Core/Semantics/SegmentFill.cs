namespace Norristown.Semantics;

/// <summary>
/// Represents the byte ld65 fills a segment's padding with, which is the space a <c>.res</c> or
/// <c>.align</c> with no fill byte of its own leaves. ld65 takes the segment's <c>fillval</c>, else
/// the <c>fillval</c> of the memory area it loads into, else zero.
/// </summary>
/// <param name="Value">The byte, or null when nt65 cannot work it out.</param>
/// <param name="Source">
/// What gives the byte, as a phrase for a message, such as <c>the memory area `ROM` in
/// `rom.cfg`</c> or <c>ld65's default</c>.
/// </param>
public sealed record SegmentFill(long? Value, string Source);
