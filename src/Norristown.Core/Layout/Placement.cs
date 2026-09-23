namespace Norristown.Layout;

/// <summary>
/// Where one line's bytes land: which run of bytes they belong to, and how far into it.
/// <para>
/// A run is a segment's bytes in the order the file writes them, across every region and
/// block of that segment, as ca65 writes them. A nested segment block's bytes are in its own
/// segment's run, at their place in the text, and the run around it continues across it. An
/// <c>.align</c> ends the run it is in, because how many bytes it generates depends on an
/// address, which nt65 never knows, and a <c>.place</c> ends every run, because the placed
/// module's bytes come in between.
/// </para>
/// </summary>
/// <param name="Stream">Which run of bytes.</param>
/// <param name="Offset">How many bytes of that stream come first.</param>
/// <param name="Length">How many bytes the line itself generates.</param>
public readonly record struct Placement(int Stream, int Offset, int Length)
{
    /// <summary>How far into the stream the line after this one starts.</summary>
    public int End => Offset + Length;
}
