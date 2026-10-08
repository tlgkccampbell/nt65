namespace Norristown.Flow;

/// <summary>Specifies how the two locations of a <see cref="SharedBytes"/> run come to take the same bytes.</summary>
public enum SharedBytesKind
{
    /// <summary>The locations are on different pages that cover some of the same addresses.</summary>
    OtherPage,

    /// <summary>
    /// The locations are on one page, and the source fixes both addresses, so the program aliases
    /// the bytes on purpose.
    /// </summary>
    Deliberate,

    /// <summary>
    /// The locations are on one page, and the layout gives at least one of them its address, as
    /// the last build or a linked configuration does, so the two collide.
    /// </summary>
    Collision,
}
