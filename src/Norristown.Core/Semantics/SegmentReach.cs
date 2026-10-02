namespace Norristown.Semantics;

/// <summary>
/// Describes whether code in one segment can reach an address in another, as the segment table
/// and the linked configurations decide it. Reach depends only on where segments are placed, so
/// nt65 tracks no mapper state to answer it.
/// </summary>
public enum SegmentReach : byte
{
    /// <summary>The address is mapped whenever the code runs, as far as nt65 can tell.</summary>
    Seen,

    /// <summary>
    /// The address is in another address space, so to this code it is only a number.
    /// </summary>
    OtherSpace,

    /// <summary>
    /// The address runs in another memory area that covers the same addresses as the one the code
    /// runs in, so the two are never mapped at the same moment and no form of addressing reaches
    /// it.
    /// </summary>
    NeverMapped,

    /// <summary>
    /// The address is in a segment whose home bank differs from the code's. A near jump, call or
    /// branch cannot reach it, but a long one can.
    /// </summary>
    OtherBank,
}
