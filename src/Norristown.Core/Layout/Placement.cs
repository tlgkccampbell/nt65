namespace Norristown.Layout;

/// <summary>
/// Where one line's bytes land: which stream of bytes they belong to, and how far into it.
/// <para>
/// A stream is one segment block's contents. A nested segment block is a detour, so the
/// stream around it continues across it, and nothing in it is at a known distance from
/// anything outside. An <c>.align</c> ends the stream it is in for the same reason: how
/// many bytes it generates depends on an address, which nt65 never knows.
/// </para>
/// </summary>
/// <param name="Stream">Which byte stream, counted from zero for the file's own.</param>
/// <param name="Offset">How many bytes of that stream come first.</param>
/// <param name="Length">How many bytes the line itself generates.</param>
public readonly record struct Placement(int Stream, int Offset, int Length)
{
    /// <summary>How far into the stream the line after this one starts.</summary>
    public int End => Offset + Length;
}
