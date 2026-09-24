namespace Norristown.LanguageServer.Protocol;

/// <summary>
/// Represents a short piece of text the editor displays within a line without inserting it into
/// the document.
/// </summary>
/// <param name="Position">The position in the line where the hint is displayed.</param>
/// <param name="Label">The hint text, which is a few characters long.</param>
/// <param name="Kind">
/// The kind of hint, or null when it is neither of the two kinds the protocol defines.
/// </param>
/// <param name="Tooltip">A sentence explaining the hint, shown when the pointer is over it.</param>
/// <param name="PaddingLeft">Whether the editor leaves a space before the hint.</param>
/// <param name="PaddingRight">Whether the editor leaves a space after the hint.</param>
internal sealed record InlayHint(
    Position Position,
    string Label,
    InlayHintKind? Kind = null,
    MarkupContent? Tooltip = null,
    bool? PaddingLeft = null,
    bool? PaddingRight = null);
