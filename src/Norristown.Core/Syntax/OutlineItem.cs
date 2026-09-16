namespace Norristown.Syntax;

/// <summary>One entry in a file's outline.</summary>
/// <param name="Kind">What it declares.</param>
/// <param name="Name">The name as the source writes it.</param>
/// <param name="Detail">A short rendering of the rest of the declaration, or null.</param>
/// <param name="Span">Everything the declaration covers, a block's body included.</param>
/// <param name="NameSpan">Just the name, which is what an editor reveals and selects.</param>
/// <param name="Children">Declarations inside it, in source order.</param>
public sealed record OutlineItem(
    OutlineKind Kind,
    string Name,
    string? Detail,
    TextSpan Span,
    TextSpan NameSpan,
    IReadOnlyList<OutlineItem> Children);
