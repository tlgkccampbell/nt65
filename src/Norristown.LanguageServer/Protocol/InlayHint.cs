namespace Norristown.LanguageServer.Protocol;

/// <summary>One short piece of text the editor draws in a line without putting it there.</summary>
/// <param name="Position">Where in the line it is drawn.</param>
/// <param name="Label">What it says, which is a few characters.</param>
/// <param name="Kind">What it is, or null where it is neither of the two the protocol names.</param>
/// <param name="Tooltip">What it means, in a sentence, shown when the hint is pointed at.</param>
/// <param name="PaddingLeft">Whether the editor leaves a space before it.</param>
/// <param name="PaddingRight">Whether the editor leaves a space after it.</param>
internal sealed record InlayHint(
    Position Position,
    string Label,
    InlayHintKind? Kind = null,
    MarkupContent? Tooltip = null,
    bool? PaddingLeft = null,
    bool? PaddingRight = null);
