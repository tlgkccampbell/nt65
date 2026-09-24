namespace Norristown.Layout;

/// <summary>
/// Represents where one line's bytes land, as the run of bytes they belong to and the offset
/// into that run.
/// <para>
/// A run is a segment's bytes in the order the file emits them, across every region and block
/// of that segment, as ca65 emits them. A nested segment block's bytes are in its own segment's
/// run, at their position in the text, and the enclosing run continues across the block. An
/// <c>.align</c> ends the run it is in, because the number of bytes it generates depends on an
/// address, which nt65 never knows. A <c>.place</c> ends every run, because the placed module's
/// bytes come in between.
/// </para>
/// </summary>
/// <param name="Stream">The run of bytes the line belongs to.</param>
/// <param name="Offset">The number of bytes of that run that come before the line.</param>
/// <param name="Length">The number of bytes the line itself generates.</param>
public readonly record struct BytePosition(int Stream, int Offset, int Length)
{
    /// <summary>Gets the offset into the run at which the line after this one starts.</summary>
    public int End => Offset + Length;
}
