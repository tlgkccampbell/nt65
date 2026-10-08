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
    /// The locations are on one page, and one of them lands there by accident of the layout. That
    /// is a segment whose predicted bytes run on into another segment's, or an address the source
    /// fixes inside a segment's bytes.
    /// </summary>
    Collision,
}
