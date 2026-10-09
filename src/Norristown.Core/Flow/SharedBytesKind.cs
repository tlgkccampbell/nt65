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
    /// The locations are on one page, and the linked configuration places both, so its author
    /// wrote the overlap. That is so when the configuration pins where both segments start, when
    /// it runs the two segments in different memory areas that cover some of the same addresses,
    /// or when the last build gives both addresses.
    /// </summary>
    Authored,

    /// <summary>
    /// The locations are on one page, and neither the source nor the configuration places both
    /// there. That is a segment whose predicted bytes run on into another segment's, or an address
    /// the source fixes inside bytes the last build gave a segment. Whether the program means it
    /// is not something the map can tell.
    /// </summary>
    Collision,

    /// <summary>
    /// The locations are on one page, and one is an address the source fixes inside a segment's
    /// bytes whose addresses are only predicted from the configuration. The two may take the same
    /// bytes, but only a build can say whether they do.
    /// </summary>
    Unverified,
}
