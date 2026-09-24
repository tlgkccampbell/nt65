namespace Norristown.Syntax;

/// <summary>Represents one entry in a file's outline.</summary>
/// <param name="Kind">The kind of declaration.</param>
/// <param name="Name">The declared name, as it appears in the source.</param>
/// <param name="Detail">A short rendering of the rest of the declaration, or null.</param>
/// <param name="Span">The whole range the declaration covers, including a block's body.</param>
/// <param name="NameSpan">The range of the name alone, which an editor reveals and selects.</param>
/// <param name="Children">The declarations inside this one, in source order.</param>
public sealed record OutlineItem(
    OutlineKind Kind,
    string Name,
    string? Detail,
    TextSpan Span,
    TextSpan NameSpan,
    IReadOnlyList<OutlineItem> Children);
